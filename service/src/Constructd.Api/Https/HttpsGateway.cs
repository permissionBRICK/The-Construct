using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Encodings.Web;
using Constructd.Api.Endpoints;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Windows.Forwards;

namespace Constructd.Api.Https;

/// <summary>One listener as the status route reports it.</summary>
public sealed record HttpsListenerStatus(string Kind, int Port, string? ForwardId, string? VmName, bool Listening, string? Error);

/// <summary>
/// THE HOST'S HTTPS FRONT (docs/https-forwards.md). It terminates TLS with the host's certificate
/// (<see cref="HttpsCertificateStore"/>) and relays the decrypted byte stream, so WebSockets, streaming and
/// request bodies of any size pass unchanged, and the <c>Host</c> header a browser sends reaches the guest
/// as it was sent (T3 compares it with the <c>Origin</c>). Two kinds of listener:
/// <list type="bullet">
/// <item>one per HTTPS forward, on the forward's own public port from the app range:
///   <c>https://&lt;vm public host&gt;:&lt;port&gt;/</c>;</item>
/// <item>the shared port (host setting <c>https.sharedPort</c>, usually 443), which routes by the TLS server
///   name: the vault web address's host gets the vault pages (relayed to an internal loopback endpoint that
///   answers nothing else), and a VM's own public name (<c>Constructd:PublicHostPattern</c>) gets that VM's
///   main HTTPS forward.</item>
/// </list>
/// When the guest does not answer, the client gets a small page that reloads itself, and a stopped or saved
/// VM is started on the owner's behalf (host setting <c>https.startOnRequest</c>).
/// </summary>
public sealed class HttpsGateway : IHttpsForwardHost, IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    // A TLS server answers a ClientHello within milliseconds; a plain one either refuses it at once or waits
    // for a request line that never comes, so detection gives up quickly. An explicit upstreamTls waits longer.
    private static readonly TimeSpan UpstreamProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestHeadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AddressTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan UpstreamModeTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RouteTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartThrottle = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SharedReconcileInterval = TimeSpan.FromSeconds(10);
    private const int MaxRequestHead = 16 * 1024;
    private const int RefreshSeconds = 5;

    private readonly IForwardStore _store;
    private readonly IVmRepository _vms;
    private readonly IHypervisorDriver _driver;
    private readonly IHostAddressResolver _addresses;
    private readonly HttpsCertificateStore _certificates;
    private readonly IHostConfigStore _config;
    private readonly ConstructdOptions _options;
    private readonly IClock _clock;
    private readonly IServiceProvider _services;
    private readonly ILogger<HttpsGateway> _logger;
    private readonly IPAddress _listenAddress;

    private readonly ConcurrentDictionary<string, Listener> _forwardListeners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _forwardErrors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StrongBox> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (IPAddress Address, DateTimeOffset Expires)> _addressCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (bool Tls, DateTimeOffset Expires)> _upstreamMode = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _startRequested = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _sharedGate = new(1, 1);
    private readonly SemaphoreSlim _routeGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    private Listener? _shared;
    private int? _sharedPortWanted;
    private string? _sharedError;
    private Task? _sharedLoop;
    private (HttpsRouteTable Table, DateTimeOffset Expires) _routes = (HttpsRouteTable.Empty(), DateTimeOffset.MinValue);

    public HttpsGateway(
        IForwardStore store,
        IVmRepository vms,
        IHypervisorDriver driver,
        HttpsCertificateStore certificates,
        IHostConfigStore config,
        ConstructdOptions options,
        IClock clock,
        IServiceProvider services,
        ILogger<HttpsGateway> logger,
        IHostAddressResolver? addresses = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _vms = vms;
        _driver = driver;
        _certificates = certificates;
        _config = config;
        _options = options;
        _clock = clock;
        _services = services;
        _logger = logger;
        _addresses = addresses ?? new DnsHostAddressResolver();
        _listenAddress = IPAddress.TryParse(options.ListenAddress, out var listen) && listen.AddressFamily == AddressFamily.InterNetwork
            ? listen
            : throw new InvalidOperationException("Constructd:ListenAddress must be an IPv4 address.");
    }

    /// <summary>The loopback port the shared listener relays the vault pages to; zero while there is none.</summary>
    public Func<int> VaultRelayPort { get; set; } = () => 0;

    // ── IHttpsForwardHost ──────────────────────────────────────────────────────────────────────────

    public void Ensure(PortForward forward)
    {
        ArgumentNullException.ThrowIfNull(forward);
        if (!forward.Https || forward.PublicPort is not int port) return;
        if (_forwardListeners.TryGetValue(forward.Id, out var existing) && existing.Port == port)
        {
            existing.Forward = forward;
            return;
        }

        var listener = new Listener(this, port, forward);
        try
        {
            listener.Start(_listenAddress);
        }
        catch (SocketException ex)
        {
            _forwardErrors[forward.Id] = $"could not listen on port {port} ({ex.SocketErrorCode})";
            throw new PortForwardException(forward.VmName, port, _forwardErrors[forward.Id]);
        }

        _forwardErrors.TryRemove(forward.Id, out _);
        _forwardListeners.AddOrUpdate(forward.Id, listener, (_, old) =>
        {
            old.DisposeAsync().AsTask().ContinueWith(static _ => { }, TaskScheduler.Default);
            return listener;
        });
        InvalidateRoutes();
    }

    public async Task StopAsync(string forwardId)
    {
        _forwardErrors.TryRemove(forwardId, out _);
        _upstreamMode.TryRemove(forwardId, out _);
        if (_forwardListeners.TryRemove(forwardId, out var listener)) await listener.DisposeAsync().ConfigureAwait(false);
        InvalidateRoutes();
    }

    public int ActiveConnections(string vmName) =>
        _connections.TryGetValue(vmName, out var box) ? Volatile.Read(ref box.Value) : 0;

    // ── status and configuration ───────────────────────────────────────────────────────────────────

    /// <summary>Every listener the gateway runs or failed to run.</summary>
    public IReadOnlyList<HttpsListenerStatus> Listeners()
    {
        var list = new List<HttpsListenerStatus>();
        if (_sharedPortWanted is int wanted)
        {
            list.Add(new("shared", wanted, null, null, _shared is { } shared && shared.Port == wanted, _sharedError));
        }

        foreach (var (id, listener) in _forwardListeners)
        {
            list.Add(new("forward", listener.Port, id, listener.Forward?.VmName, true, null));
        }

        foreach (var (id, error) in _forwardErrors)
        {
            if (!_forwardListeners.ContainsKey(id)) list.Add(new("forward", 0, id, null, false, error));
        }

        return list;
    }

    /// <summary>The configured shared port, or null when the shared listener is off.</summary>
    public async Task<HttpsConfig> ConfigAsync(CancellationToken ct) =>
        await _config.GetAsync<HttpsConfig>("https", ct).ConfigureAwait(false) ?? HostAdminDefaults.Https;

    /// <summary>Applies the host setting and the certificate state to the shared listener now.</summary>
    public async Task ReconcileSharedAsync(CancellationToken ct)
    {
        await _sharedGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var config = await ConfigAsync(ct).ConfigureAwait(false);
            _sharedPortWanted = config.SharedPort;
            var wanted = config.SharedPort is int port && _certificates.Current is not null ? port : (int?)null;
            if (_shared is { } current && current.Port == wanted) return;
            if (_shared is { } old)
            {
                _shared = null;
                await old.DisposeAsync().ConfigureAwait(false);
            }

            _sharedError = config.SharedPort is null ? null : _certificates.Current is null ? "no certificate is installed" : null;
            if (wanted is not int bind) return;
            var listener = new Listener(this, bind, null);
            try
            {
                listener.Start(_listenAddress);
                _shared = listener;
                _logger.LogInformation("HTTPS shared port {Port} is listening.", bind);
            }
            catch (SocketException ex)
            {
                _sharedError = $"could not listen on port {bind} ({ex.SocketErrorCode})";
                _logger.LogWarning("HTTPS shared port: {Error}", _sharedError);
            }
        }
        finally
        {
            _sharedGate.Release();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedLoop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await ReconcileSharedAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("HTTPS shared port reconcile failed: {Error}", SafeError.Describe(ex));
                }

                try
                {
                    await Task.Delay(SharedReconcileInterval, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
        return Task.CompletedTask;
    }

    async Task IHostedService.StopAsync(CancellationToken cancellationToken) => await DisposeAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested) await _stop.CancelAsync().ConfigureAwait(false);
        if (_sharedLoop is not null)
        {
            try { await _sharedLoop.ConfigureAwait(false); } catch (Exception) { /* stopping */ }
        }

        if (_shared is { } shared) await shared.DisposeAsync().ConfigureAwait(false);
        _shared = null;
        foreach (var id in _forwardListeners.Keys.ToArray()) await StopAsync(id).ConfigureAwait(false);
    }

    // ── one connection ─────────────────────────────────────────────────────────────────────────────

    private abstract record Route(string? VmName);

    private sealed record ForwardRoute(PortForward Forward) : Route(Forward.VmName);

    private sealed record VaultRoute() : Route((string?)null);

    private sealed record NoRoute(string ServerName) : Route((string?)null);

    private async Task HandleAsync(TcpClient client, Listener listener)
    {
        using (client)
        {
            client.NoDelay = true;
            var certificate = _certificates.Current;
            if (certificate is null) return;

            await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                handshake.CancelAfter(HandshakeTimeout);
                try
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificateContext = certificate.Context,
                        ApplicationProtocols = [SslApplicationProtocol.Http11],
                        ClientCertificateRequired = false,
                    }, handshake.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException or SocketException)
                {
                    return;
                }
            }

            var serverName = tls.TargetHostName ?? string.Empty;
            var route = listener.Forward is { } fixedForward ? new ForwardRoute(fixedForward) : await SharedRouteAsync(serverName).ConfigureAwait(false);
            var counter = route.VmName is { } vm ? _connections.GetOrAdd(vm, _ => new StrongBox()) : null;
            if (counter is not null) Interlocked.Increment(ref counter.Value);
            try
            {
                if (route is NoRoute missing)
                {
                    await RespondAsync(tls, 404, "Not published", $"Nothing is published under {(missing.ServerName.Length > 0 ? missing.ServerName : "this address")}.", refresh: false).ConfigureAwait(false);
                    return;
                }

                await using var upstream = await ConnectAsync(route, serverName).ConfigureAwait(false);
                if (upstream is null)
                {
                    await UnavailableAsync(tls, route).ConfigureAwait(false);
                    return;
                }

                await PumpAsync(tls, upstream).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug("HTTPS relay ended: {Error}", ex.GetType().Name);
            }
            finally
            {
                if (counter is not null) Interlocked.Decrement(ref counter.Value);
            }
        }
    }

    private async Task<Route> SharedRouteAsync(string serverName)
    {
        var name = serverName.Trim().TrimEnd('.');
        var table = await RoutesAsync().ConfigureAwait(false);
        if (table.VaultHost is not null && string.Equals(name, table.VaultHost, StringComparison.OrdinalIgnoreCase)) return new VaultRoute();
        return name.Length > 0 && table.Hosts.TryGetValue(name, out var forward) ? new ForwardRoute(forward) : new NoRoute(name);
    }

    /// <summary>
    /// The names the shared port serves (<see cref="HttpsRouteTable"/>: the vault host, each VM's main forward under
    /// its own name, each HTTPS forward as &lt;vm&gt;-&lt;name&gt;); rebuilt at most every few seconds.
    /// </summary>
    private async Task<HttpsRouteTable> RoutesAsync()
    {
        var now = _clock.UtcNow;
        var cached = _routes;
        if (cached.Expires > now) return cached.Table;
        await _routeGate.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            if (_routes.Expires > now) return _routes.Table;
            var table = await BuildRoutesAsync(_store, _vms, _config, _options, _stop.Token).ConfigureAwait(false);
            _routes = (table, now + RouteTtl);
            return table;
        }
        finally
        {
            _routeGate.Release();
        }
    }

    /// <summary>
    /// The route table from the stores, uncached: what the gateway serves and what the API advertises come from the
    /// same rules (docs/https-forwards.md, "The shared HTTPS port").
    /// </summary>
    internal static async Task<HttpsRouteTable> BuildRoutesAsync(IForwardStore store, IVmRepository vms, IHostConfigStore config,
        ConstructdOptions options, CancellationToken ct)
    {
        var vault = await config.GetAsync<VaultConfig>("vault", ct).ConfigureAwait(false);
        var (url, _) = VaultWebAddress.Resolve(vault?.WebUrl, options);
        var vaultHost = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.IdnHost : null;
        if (string.IsNullOrWhiteSpace(options.PublicHostPattern)) return HttpsRouteTable.Empty(vaultHost);
        var primaries = (await vms.ListAsync(owner: null, ct).ConfigureAwait(false))
            .Where(v => v.Kind == VmKind.Primary && !v.Deleting)
            .Select(v => v.Name)
            .ToList();
        var forwards = await store.ListAsync(vmName: null, ct).ConfigureAwait(false);
        return HttpsRouteTable.Build(forwards, primaries, options.PublicHostPattern, options.PublicHost, vaultHost);
    }

    private void InvalidateRoutes() => _routes = (_routes.Table, DateTimeOffset.MinValue);

    /// <summary>The upstream stream, or null when the guest (or the vault endpoint) does not answer.</summary>
    private async Task<Stream?> ConnectAsync(Route route, string serverName)
    {
        if (route is VaultRoute)
        {
            var port = VaultRelayPort();
            return port > 0 ? await TryConnectAsync(new IPEndPoint(IPAddress.Loopback, port)).ConfigureAwait(false) : null;
        }

        var forward = ((ForwardRoute)route).Forward;
        IPEndPoint endpoint;
        try
        {
            endpoint = new IPEndPoint(await ResolveAsync(forward.VmName).ConfigureAwait(false), forward.VmPort);
        }
        catch (Exception ex) when (ex is InvalidOperationException or SocketException)
        {
            return null;
        }

        var mode = forward.UpstreamTls
                   ?? (_upstreamMode.TryGetValue(forward.Id, out var known) && known.Expires > _clock.UtcNow ? known.Tls : (bool?)null);
        if (mode != false)
        {
            var stream = await TryConnectAsync(endpoint).ConfigureAwait(false);
            if (stream is null)
            {
                _addressCache.TryRemove(forward.VmName, out _);
                return null;
            }

            var upstreamTls = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            probe.CancelAfter(forward.UpstreamTls == true ? HandshakeTimeout : UpstreamProbeTimeout);
            try
            {
                await upstreamTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = serverName.Length > 0 ? serverName : _options.PublicHostFor(forward.VmName),
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                }, probe.Token).ConfigureAwait(false);
                _upstreamMode[forward.Id] = (true, _clock.UtcNow + UpstreamModeTtl);
                return upstreamTls;
            }
            catch (Exception ex) when ((ex is AuthenticationException or IOException or OperationCanceledException) && !_stop.IsCancellationRequested)
            {
                await upstreamTls.DisposeAsync().ConfigureAwait(false);
                if (forward.UpstreamTls == true) return null;
                _upstreamMode[forward.Id] = (false, _clock.UtcNow + UpstreamModeTtl);
            }
        }

        var plain = await TryConnectAsync(endpoint).ConfigureAwait(false);
        if (plain is null) _addressCache.TryRemove(forward.VmName, out _);
        return plain;
    }

    private async Task<Stream?> TryConnectAsync(IPEndPoint endpoint)
    {
        var client = new TcpClient { NoDelay = true };
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        connect.CancelAfter(ConnectTimeout);
        try
        {
            await client.ConnectAsync(endpoint, connect.Token).ConfigureAwait(false);
            return new OwnedStream(client);
        }
        catch (Exception ex) when ((ex is SocketException or OperationCanceledException) && !_stop.IsCancellationRequested)
        {
            client.Dispose();
            return null;
        }
    }

    private async Task<IPAddress> ResolveAsync(string vmName)
    {
        if (_addressCache.TryGetValue(vmName, out var cached) && cached.Expires > _clock.UtcNow) return cached.Address;
        var endpoint = await _driver.GetEndpointAsync(vmName, _stop.Token).ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"The driver reports no address for VM '{vmName}' yet.");
        var address = await _addresses.ResolveIPv4Async(endpoint.SshHost, _stop.Token).ConfigureAwait(false)
                      ?? throw new InvalidOperationException($"The endpoint of VM '{vmName}' does not resolve to an IPv4 address.");
        _addressCache[vmName] = (address, _clock.UtcNow + AddressTtl);
        return address;
    }

    private async Task PumpAsync(Stream client, Stream upstream)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var up = CopyAsync(client, upstream, session.Token);
        var down = CopyAsync(upstream, client, session.Token);
        await Task.WhenAny(up, down).ConfigureAwait(false);
        // Give the other direction a moment to drain after one side closed, then end the session.
        try
        {
            await Task.WhenAny(Task.WhenAll(up, down), Task.Delay(TimeSpan.FromSeconds(2), session.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }

        await session.CancelAsync().ConfigureAwait(false);
    }

    private static async Task CopyAsync(Stream from, Stream to, CancellationToken token)
    {
        try
        {
            await from.CopyToAsync(to, token).ConfigureAwait(false);
            await to.FlushAsync(token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The peer went away; the session ends with the other direction.
        }
    }

    // ── when the guest does not answer ─────────────────────────────────────────────────────────────

    private async Task UnavailableAsync(SslStream client, Route route)
    {
        if (route is VaultRoute)
        {
            await RespondAsync(client, 502, "Unavailable", "The key vault pages are not available on this port right now.", refresh: true).ConfigureAwait(false);
            return;
        }

        var forward = ((ForwardRoute)route).Forward;
        var vm = await _vms.GetAsync(forward.VmName, _stop.Token).ConfigureAwait(false);
        if (vm is null || vm.Deleting)
        {
            await RespondAsync(client, 404, "Not published", $"{forward.VmName} no longer exists.", refresh: false).ConfigureAwait(false);
            return;
        }

        var state = await _driver.GetStateAsync(vm.Name, _stop.Token).ConfigureAwait(false);
        string message;
        if (state == VmState.Running)
        {
            message = $"{vm.Name} is running, but nothing answers on port {forward.VmPort} yet.";
        }
        else if ((await ConfigAsync(_stop.Token).ConfigureAwait(false)).StartOnRequest)
        {
            RequestStart(vm.Name);
            message = $"Starting {vm.Name}. This takes a moment.";
        }
        else
        {
            message = $"{vm.Name} is {state.ToString().ToLowerInvariant()}. Start it to use this address.";
        }

        await RespondAsync(client, 503, "Starting", message, refresh: true).ConfigureAwait(false);
    }

    /// <summary>Starts the VM in the background, at most once per <see cref="StartThrottle"/>.</summary>
    private void RequestStart(string vmName)
    {
        var now = _clock.UtcNow;
        if (_startRequested.TryGetValue(vmName, out var last) && now - last < StartThrottle) return;
        _startRequested[vmName] = now;
        _ = Task.Run(async () =>
        {
            string outcome;
            try
            {
                outcome = await StartVmAsync(vmName, _stop.Token).ConfigureAwait(false) ?? "started";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcome = SafeError.Describe(ex);
            }

            _logger.LogInformation("HTTPS start-on-request for {Vm}: {Outcome}", vmName, outcome);
            try
            {
                await _services.GetRequiredService<IAuditLog>().AppendAsync(new AuditEntry(_clock.UtcNow, "system", "vm.power", vmName,
                    outcome == "started" ? AuditOutcome.Success : AuditOutcome.Failure, $"action=start, initiator=https-start-on-request, outcome={outcome}"),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Could not audit an HTTPS start-on-request: {Error}", SafeError.Describe(ex));
            }
        });
    }

    /// <summary>The primary power start of <c>POST /vms/{name}/power</c>, on the owner's behalf. Null when started.</summary>
    private async Task<string?> StartVmAsync(string vmName, CancellationToken ct)
    {
        var gates = _services.GetRequiredService<IVmOperationGate>();
        await using var held = await PrimaryOperationGate.AcquireAsync(gates, vmName, "https-start-on-request", ct).ConfigureAwait(false);
        if (held is null) return "operation-in-progress";
        var vm = await _vms.GetAsync(vmName, ct).ConfigureAwait(false);
        if (vm is null || vm.Deleting || vm.Kind != VmKind.Primary) return "unavailable";
        if (await LifecycleEndpoints.LiveAsync(vm, _services, ct).ConfigureAwait(false)) return "operation-in-progress";
        if (await _driver.GetStateAsync(vm.Name, ct).ConfigureAwait(false) == VmState.Running) return null;
        var key = new OperationKeyRecord(vm.Owner, "lifecycle-start", Guid.NewGuid().ToString("n"), "primary-power-start:" + vm.Name,
            vm.Name, null, OperationKeyState.InFlight, null, vm.PowerGeneration, null, _clock.UtcNow);
        try
        {
            var reply = await _services.GetRequiredService<LifecycleStart>().RunAsync(vm, null, null, key, ct).ConfigureAwait(false);
            return reply.Code;
        }
        catch (LifecycleException ex)
        {
            return ex.Capacity is not null ? "capacity" : ex.Code;
        }
    }

    /// <summary>
    /// Answers the client's request with a small HTML page (plain text for anything that is not a page
    /// load) and closes. The request head is read first, so the client is not cut off mid-send.
    /// </summary>
    private static async Task RespondAsync(SslStream client, int status, string title, string message, bool refresh)
    {
        var head = await ReadRequestHeadAsync(client).ConfigureAwait(false);
        var firstLine = head.Split("\r\n", 2)[0];
        var method = firstLine.Split(' ', 2)[0];
        var wantsPage = method is "GET" or "HEAD" && !head.Contains("\r\nUpgrade:", StringComparison.OrdinalIgnoreCase);
        var body = wantsPage
            ? $"<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">" +
              (refresh ? $"<meta http-equiv=\"refresh\" content=\"{RefreshSeconds}\">" : "") +
              $"<title>{HtmlEncoder.Default.Encode(title)}</title><style>body{{font:16px system-ui,sans-serif;margin:3em auto;max-width:36em;padding:0 1em;color:#222;background:#fafafa}}@media(prefers-color-scheme:dark){{body{{color:#ddd;background:#181818}}}}</style></head>" +
              $"<body><p>{HtmlEncoder.Default.Encode(message)}</p>{(refresh ? "<p>This page reloads by itself.</p>" : "")}</body></html>"
            : message + "\n";
        var bytes = Encoding.UTF8.GetBytes(body);
        var reason = status switch { 404 => "Not Found", 502 => "Bad Gateway", _ => "Service Unavailable" };
        var response = new StringBuilder()
            .Append($"HTTP/1.1 {status} {reason}\r\n")
            .Append($"Content-Type: {(wantsPage ? "text/html" : "text/plain")}; charset=utf-8\r\n")
            .Append($"Content-Length: {bytes.Length}\r\n")
            .Append("Cache-Control: no-store\r\n");
        if (refresh) response.Append($"Retry-After: {RefreshSeconds}\r\n");
        response.Append("Connection: close\r\n\r\n");
        try
        {
            await client.WriteAsync(Encoding.ASCII.GetBytes(response.ToString())).ConfigureAwait(false);
            if (method != "HEAD") await client.WriteAsync(bytes).ConfigureAwait(false);
            await client.FlushAsync().ConfigureAwait(false);
            await client.ShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The client is gone.
        }
    }

    private static async Task<string> ReadRequestHeadAsync(Stream client)
    {
        var buffer = new byte[4096];
        var head = new MemoryStream();
        using var timeout = new CancellationTokenSource(RequestHeadTimeout);
        try
        {
            while (head.Length < MaxRequestHead)
            {
                var read = await client.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                head.Write(buffer, 0, read);
                if (Encoding.ASCII.GetString(head.GetBuffer(), 0, (int)head.Length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // Answer with what arrived.
        }

        return Encoding.ASCII.GetString(head.GetBuffer(), 0, (int)head.Length);
    }

    // ── plumbing ───────────────────────────────────────────────────────────────────────────────────

    private sealed class StrongBox
    {
        public int Value;
    }

    /// <summary>A network stream that also disposes its client.</summary>
    private sealed class OwnedStream(TcpClient client) : Stream
    {
        private readonly NetworkStream _inner = client.GetStream();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                client.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>One TLS listening port: a forward's own (<see cref="Forward"/> set) or the shared one.</summary>
    private sealed class Listener(HttpsGateway gateway, int port, PortForward? forward) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private TcpListener? _listener;
        private Task? _accepting;

        public int Port { get; } = port;

        public PortForward? Forward { get; set; } = forward;

        public void Start(IPAddress address)
        {
            _listener = new TcpListener(address, Port);
            _listener.Start();
            _accepting = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener!.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    continue;
                }

                _ = gateway.HandleAsync(client, this);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_stop.IsCancellationRequested) await _stop.CancelAsync().ConfigureAwait(false);
            _listener?.Stop();
            if (_accepting is not null)
            {
                try { await _accepting.ConfigureAwait(false); } catch (Exception) { /* stopping */ }
            }

            _stop.Dispose();
        }
    }
}
