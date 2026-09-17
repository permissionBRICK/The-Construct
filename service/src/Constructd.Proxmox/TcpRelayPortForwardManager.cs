using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Windows.Forwards;
using Constructd.Windows.Internal;
using Microsoft.Extensions.Logging;

namespace Constructd.Proxmox;

/// <summary>
/// <see cref="IPortForwardManager"/> that relays TCP in-process: every materialized forward is a
/// listener on the service host's <c>Constructd:ListenAddress</c> whose accepted connections are
/// piped to the guest. The bookkeeping — the two port ranges, the durable-before-live ordering, the
/// per-VM gate and cap, reconciliation from the store at startup — is the same as the Windows
/// implementation's; only the materialization differs: a socket owned by this process instead of a
/// <c>netsh</c> rule.
///
/// Why a relay rather than DNAT: the guests sit on the node's LAN bridge with DHCP leases, so a
/// kernel forward would need masquerading and connection tracking, and the idle signal would have to
/// read conntrack. A socket this process owns knows its own connection count exactly, needs no
/// netfilter rules and no sysctl, and resolves the guest's current address at connect time — which
/// is when a DHCP change matters.
/// </summary>
public sealed class TcpRelayPortForwardManager : IPortForwardManager, IAsyncDisposable
{
    private static readonly TimeSpan AddressTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly IClock _clock;
    private readonly IVmRepository _vms;
    private readonly IForwardStore _store;
    private readonly IHypervisorDriver _driver;
    private readonly IHostAddressResolver _addresses;
    private readonly ILogger<TcpRelayPortForwardManager> _logger;
    private readonly IPAddress _listenAddress;
    private readonly PortAllocator _sshPorts;
    private readonly PortAllocator _appPorts;
    private readonly ConcurrentDictionary<string, int> _sshForwards = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RelayListener> _listeners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _vmGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Address, int SshPort, DateTimeOffset Expires)> _addressCache = new(StringComparer.OrdinalIgnoreCase);

    public TcpRelayPortForwardManager(
        IClock clock,
        IVmRepository vms,
        IForwardStore store,
        IHypervisorDriver driver,
        IHostAddressResolver addresses,
        ConstructdOptions options,
        ILogger<TcpRelayPortForwardManager> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _clock = clock;
        _vms = vms;
        _store = store;
        _driver = driver;
        _addresses = addresses;
        _logger = logger;

        var ssh = options.SshForwardPorts;
        var app = options.AppForwardPorts;
        if (ssh.Start <= app.End && app.Start <= ssh.End)
        {
            throw new InvalidOperationException(
                "Constructd:SshForwardPorts and Constructd:AppForwardPorts overlap. The two ranges are " +
                "allocated independently, so an overlap hands the same public port to two VMs.");
        }

        _listenAddress = IPAddress.Parse(ArgumentGuard.IPv4(options.ListenAddress, "Constructd:ListenAddress"));
        _sshPorts = new PortAllocator(ssh.Start, ssh.End);
        _appPorts = new PortAllocator(app.Start, app.End);
    }

    /// <summary>The public ports currently listening, keyed by forward (test/diagnostics).</summary>
    public IReadOnlyDictionary<string, int> Listening =>
        _listeners.ToDictionary(pair => pair.Key, pair => pair.Value.Port, StringComparer.Ordinal);

    public async Task<int> AllocateSshForwardAsync(string vmName, CancellationToken cancellationToken)
    {
        var gate = GateFor(vmName);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sshForwards.TryGetValue(vmName, out var known))
            {
                EnsureListener(SshKey(vmName), known, vmName, ct => ResolveVmAsync(vmName, null, ct));
                return known;
            }

            var vm = await _vms.GetAsync(vmName, cancellationToken).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"Unknown VM '{vmName}'.");

            if (vm.SshForwardPort is int stored)
            {
                _sshPorts.TryReserve(stored);
                _sshForwards[vmName] = stored;
                EnsureListener(SshKey(vmName), stored, vmName, ct => ResolveVmAsync(vmName, null, ct));
                return stored;
            }

            var port = _sshPorts.Allocate(CanBind);
            try
            {
                await _vms.UpdateAsync(vm with { SshForwardPort = port }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _sshPorts.Release(port);
                throw;
            }

            _sshForwards[vmName] = port;
            EnsureListener(SshKey(vmName), port, vmName, ct => ResolveVmAsync(vmName, null, ct));
            return port;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> ReleaseSshForwardAsync(string vmName, CancellationToken cancellationToken)
    {
        var gate = GateFor(vmName);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var vm = await _vms.GetAsync(vmName, cancellationToken).ConfigureAwait(false);
            var storedPort = vm?.SshForwardPort;

            var released = _sshForwards.TryRemove(vmName, out var port);
            if (released)
            {
                _sshPorts.Release(port);
            }

            if (storedPort is int durable)
            {
                _sshPorts.Release(durable);
                await _vms.UpdateAsync(vm! with { SshForwardPort = null }, cancellationToken).ConfigureAwait(false);
                released = true;
            }

            await StopListenerAsync(SshKey(vmName)).ConfigureAwait(false);
            _addressCache.TryRemove(vmName, out _);
            return released;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<AddForwardResult> TryAddForwardAsync(string vmName, int vmPort, ForwardTarget target,
        string label, int maxForwards, CancellationToken cancellationToken) =>
        AddForwardAsync(vmName, vmPort, target, label, maxForwards, cancellationToken, null);

    public Task<AddForwardResult> TryAddDestinationForwardAsync(ForwardRequest request,
        ForwardDestination destination, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.TargetVm, destination.VmName, StringComparison.OrdinalIgnoreCase) ||
            request.ConnectPort != destination.ConnectPort || destination.ConnectPort is < 1 or > 65535 ||
            (request.Target == ForwardTarget.Host && (!destination.Verified || destination.ConnectAddress is null)))
        {
            throw new InvalidOperationException("Invalid or unverified forward destination.");
        }

        return AddForwardAsync(request.TargetVm, request.VmPort, request.Target, request.Label,
            request.MaxForwards, cancellationToken, destination);
    }

    private async Task<AddForwardResult> AddForwardAsync(
        string vmName,
        int vmPort,
        ForwardTarget target,
        string label,
        int maxForwards,
        CancellationToken cancellationToken,
        ForwardDestination? destination)
    {
        var gate = GateFor(vmName);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var vm = await _vms.GetAsync(vmName, cancellationToken).ConfigureAwait(false);
            if (vm is null || vm.Deleting)
            {
                return AddForwardResult.VmUnavailable;
            }

            var existing = await _store.CountByVmAsync(vmName, cancellationToken).ConfigureAwait(false);
            if (existing >= maxForwards)
            {
                return AddForwardResult.LimitReached;
            }

            int? publicPort = target == ForwardTarget.Host ? _appPorts.Allocate(CanBind) : null;
            var forward = new PortForward(
                Id: Guid.NewGuid().ToString("n"),
                VmName: vmName,
                VmPort: ArgumentGuard.Port(vmPort, "vm port"),
                PublicPort: publicPort,
                Target: target,
                Label: label,
                Created: _clock.UtcNow,
                Destination: destination);

            try
            {
                await _store.AddAsync(forward, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (publicPort is int failed)
                {
                    _appPorts.Release(failed);
                }

                throw;
            }

            if (publicPort is int port)
            {
                EnsureListener(forward.Id, port, vmName, TargetFor(forward));
            }

            return AddForwardResult.Added(forward);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> RemoveForwardAsync(string vmName, string id, CancellationToken cancellationToken)
    {
        var forward = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (forward is null || !string.Equals(forward.VmName, vmName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!await _store.RemoveAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await ReleasePublicPortAsync(forward).ConfigureAwait(false);
        return true;
    }

    public async Task<int> RemoveAllForwardsAsync(string vmName, CancellationToken cancellationToken)
    {
        var gate = GateFor(vmName);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var removed = 0;
            foreach (var forward in await _store.ListAsync(vmName, cancellationToken).ConfigureAwait(false))
            {
                if (await _store.RemoveAsync(forward.Id, cancellationToken).ConfigureAwait(false))
                {
                    await ReleasePublicPortAsync(forward).ConfigureAwait(false);
                    removed++;
                }
            }

            return removed;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<IReadOnlyList<PortForward>> ListAsync(string? vmName, CancellationToken cancellationToken) =>
        _store.ListAsync(vmName, cancellationToken);

    /// <summary>
    /// Rebuilds the live listeners from the durable state: every stored SSH allocation and every
    /// host-target forward gets its port reserved and its listener started if it is not already.
    /// Returns how many listeners were (re)started.
    /// </summary>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        var repaired = 0;

        foreach (var vm in await _vms.ListAsync(owner: null, cancellationToken).ConfigureAwait(false))
        {
            if (vm.SshForwardPort is not int port)
            {
                continue;
            }

            _sshForwards[vm.Name] = port;
            _sshPorts.TryReserve(port);
            if (EnsureListener(SshKey(vm.Name), port, vm.Name, ct => ResolveVmAsync(vm.Name, null, ct)))
            {
                repaired++;
            }
        }

        foreach (var forward in await _store.ListAsync(vmName: null, cancellationToken).ConfigureAwait(false))
        {
            if (forward.PublicPort is not int port)
            {
                continue;
            }

            _appPorts.TryReserve(port);
            if (EnsureListener(forward.Id, port, forward.VmName, TargetFor(forward)))
            {
                repaired++;
            }
        }

        return repaired;
    }

    /// <summary>Live relayed connections across all of the VM's listeners — the idle signal.</summary>
    public Task<int> CountActiveConnectionsAsync(string vmName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var total = _listeners.Values
            .Where(listener => string.Equals(listener.VmName, vmName, StringComparison.OrdinalIgnoreCase))
            .Sum(listener => listener.ActiveConnections);
        return Task.FromResult(total);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var key in _listeners.Keys.ToArray())
        {
            await StopListenerAsync(key).ConfigureAwait(false);
        }
    }

    // ── Targets ─────────────────────────────────────────────────────────────────

    private Func<CancellationToken, Task<IPEndPoint>> TargetFor(PortForward forward)
    {
        if (forward.Destination is { ConnectAddress: not null } destination)
        {
            var fixedTarget = new IPEndPoint(IPAddress.Parse(ArgumentGuard.IPv4(destination.ConnectAddress, "connect address")),
                ArgumentGuard.Port(destination.ConnectPort, "connect port"));
            return _ => Task.FromResult(fixedTarget);
        }

        var vmName = forward.VmName;
        var vmPort = forward.VmPort;
        return ct => ResolveVmAsync(vmName, vmPort, ct);
    }

    /// <summary>
    /// The guest's current address, from the driver, cached briefly; a relay that fails to connect
    /// drops the cache entry so the next connection asks again (a rebooted guest may hold a new lease).
    /// </summary>
    private async Task<IPEndPoint> ResolveVmAsync(string vmName, int? port, CancellationToken cancellationToken)
    {
        if (_addressCache.TryGetValue(vmName, out var cached) && cached.Expires > _clock.UtcNow)
        {
            return new IPEndPoint(IPAddress.Parse(cached.Address), port ?? cached.SshPort);
        }

        var endpoint = await _driver.GetEndpointAsync(vmName, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"The driver reports no address for VM '{vmName}' yet.");
        var address = await _addresses.ResolveIPv4Async(endpoint.SshHost, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException($"The endpoint of VM '{vmName}' does not resolve to an IPv4 address.");
        var sshPort = ArgumentGuard.Port(endpoint.SshPort, "vm ssh port");

        _addressCache[vmName] = (address.ToString(), sshPort, _clock.UtcNow + AddressTtl);
        return new IPEndPoint(address, port ?? sshPort);
    }

    // ── Listeners ───────────────────────────────────────────────────────────────

    /// <summary>Starts the listener for a forward if none is running. True when one was started.</summary>
    private bool EnsureListener(string key, int publicPort, string vmName, Func<CancellationToken, Task<IPEndPoint>> target)
    {
        if (_listeners.TryGetValue(key, out var existing) && existing.Port == publicPort)
        {
            return false;
        }

        var listener = new RelayListener(_listenAddress, publicPort, vmName, target,
            () => _addressCache.TryRemove(vmName, out _), _logger);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new PortForwardException(vmName, publicPort, $"could not listen on port {publicPort} ({ex.SocketErrorCode})");
        }

        _listeners.AddOrUpdate(key, listener, (unused, old) =>
        {
            old.DisposeAsync().AsTask().ContinueWith(static _ => { }, TaskScheduler.Default);
            return listener;
        });
        return true;
    }

    private async Task StopListenerAsync(string key)
    {
        if (_listeners.TryRemove(key, out var listener))
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ReleasePublicPortAsync(PortForward forward)
    {
        if (forward.PublicPort is int port)
        {
            _appPorts.Release(port);
            await StopListenerAsync(forward.Id).ConfigureAwait(false);
        }
    }

    private bool CanBind(int port)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(_listenAddress, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string SshKey(string vmName) => $"ssh:{vmName}";

    private SemaphoreSlim GateFor(string vmName) => _vmGates.GetOrAdd(vmName, _ => new SemaphoreSlim(1, 1));

    /// <summary>One listening port, relaying each accepted connection to the resolved target.</summary>
    private sealed class RelayListener(
        IPAddress listenAddress,
        int port,
        string vmName,
        Func<CancellationToken, Task<IPEndPoint>> target,
        Action onConnectFailure,
        ILogger logger) : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(listenAddress, port);
        private readonly CancellationTokenSource _stop = new();
        private Task? _accepting;
        private int _active;

        public int Port { get; } = port;

        public string VmName { get; } = vmName;

        public int ActiveConnections => Volatile.Read(ref _active);

        public void Start()
        {
            _listener.Start();
            _accepting = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            var token = _stop.Token;
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    logger.LogWarning("Relay on port {Port} could not accept a connection ({Error}).", Port, ex.SocketErrorCode);
                    continue;
                }

                _ = RelayAsync(client, token);
            }
        }

        private async Task RelayAsync(TcpClient client, CancellationToken token)
        {
            Interlocked.Increment(ref _active);
            using var upstream = new TcpClient();
            try
            {
                client.NoDelay = true;
                upstream.NoDelay = true;

                var endpoint = await target(token).ConfigureAwait(false);
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    connect.CancelAfter(ConnectTimeout);
                    try
                    {
                        await upstream.ConnectAsync(endpoint, connect.Token).ConfigureAwait(false);
                    }
                    catch (Exception) when (!token.IsCancellationRequested)
                    {
                        onConnectFailure();
                        throw;
                    }
                }

                using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
                var downstreamStream = client.GetStream();
                var upstreamStream = upstream.GetStream();
                var up = PumpAsync(downstreamStream, upstreamStream, upstream.Client, session.Token);
                var down = PumpAsync(upstreamStream, downstreamStream, client.Client, session.Token);
                await Task.WhenAny(up, down).ConfigureAwait(false);
                // Give the other direction a moment to drain after one side closed, then end the session.
                await Task.WhenAny(Task.WhenAll(up, down), Task.Delay(TimeSpan.FromSeconds(2), session.Token))
                    .ConfigureAwait(false);
                session.Cancel();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation("Relay on port {Port} to {Vm} ended: {Error}", Port, VmName, ex.GetType().Name);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
                client.Dispose();
            }
        }

        private static async Task PumpAsync(NetworkStream from, NetworkStream to, Socket toSocket, CancellationToken token)
        {
            try
            {
                await from.CopyToAsync(to, token).ConfigureAwait(false);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                // The peer went away; the half-close below tells the other side.
            }

            try
            {
                toSocket.Shutdown(SocketShutdown.Send);
            }
            catch (Exception)
            {
                // Already closed.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            if (_accepting is not null)
            {
                try
                {
                    await _accepting.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Stopping.
                }
            }

            _stop.Dispose();
        }
    }
}
