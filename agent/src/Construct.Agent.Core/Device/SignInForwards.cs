using System.Text.Json.Nodes;

namespace Construct.Agent.Device;

/// <summary>A sign-in's redirect forwarded from this device: the port on 127.0.0.1 here and on the VM.</summary>
public sealed record SignInForwardView(int Port);

/// <param name="Agent">The agent signing in (<c>claude-code</c>, <c>codex</c>, <c>opencode</c>, <c>antigravity</c>).</param>
/// <param name="Session">The sign-in's id, as the engine named it.</param>
public sealed record SignInForwardRequest(string Agent, string Session);

/// <summary>
/// A coding agent's sign-in without a pasted code (design 11.8), as the Claude Code extension does over Remote-SSH:
/// the CLI on the VM listens on a loopback port and its sign-in link sends the browser back to
/// <c>http://localhost:&lt;port&gt;</c>. For a sign-in the user started on one of their own VMs, the engine says which
/// link and port; this device forwards its own <c>127.0.0.1:&lt;port&gt;</c> to the VM's over the VM's SSH link, opens the
/// link in the default browser, and closes the forward when the sign-in is over (the engine says so, another sign-in on
/// the VM takes its place, or <see cref="Limit"/> passes). One per VM. The link carries the sign-in's state: it goes to
/// the browser only, never into an answer or a log.
/// </summary>
public sealed class SignInForwards(VmLinks links, IDeviceEngines engines, IDevicePlatform platform) : IAsyncDisposable
{
    private static readonly string[] Agents = ["claude-code", "codex", "opencode", "antigravity"];
    private readonly Dictionary<string, Forward> _open = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _serial = new(1, 1);

    /// <summary>The longest a forward stays open: a sign-in's own limit on the VM.</summary>
    public TimeSpan Limit { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>How often the engine is asked whether the sign-in still runs.</summary>
    public TimeSpan Poll { get; set; } = TimeSpan.FromSeconds(3);

    public async Task<SignInForwardView> StartAsync(string engine, string vm, SignInForwardRequest request, CancellationToken ct)
    {
        if (!Agents.Contains(request.Agent) || request.Session is not { Length: > 0 and <= 64 })
            throw new AgentFailure(400, "invalid-request", "agent and session are required.");
        links.Target(engine, vm);

        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Asked inside the lock: a start whose answer comes late cannot replace a newer sign-in's forward, because
            // the newer start asks again after it and finds its own sign-in.
            var (url, port) = await RedirectAsync(engine, vm, request, ct).ConfigureAwait(false)
                ?? throw new AgentFailure(409, "no-browser-sign-in", "That sign-in does not run, or its link does not return to this machine.");
            await CloseAsync(Key(engine, vm)).ConfigureAwait(false);
            var tunnel = await links.OpenLoopbackTunnelAsync(engine, vm, port, ct).ConfigureAwait(false)
                ?? throw new AgentFailure(409, "port-taken", $"Port {port} is in use on this device.");
            var forward = new Forward(request.Session, port, tunnel);
            try { await platform.OpenAsync(url, ct).ConfigureAwait(false); }
            catch
            {
                await tunnel.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            lock (_open) _open[Key(engine, vm)] = forward;
            forward.Watch = WatchAsync(engine, vm, request, forward);
            return new(port);
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>Closes the VM's sign-in forward when it is the one of <paramref name="session"/>; a newer sign-in's stays.</summary>
    public async Task StopAsync(string engine, string vm, string session, CancellationToken ct)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_open)
                if (!_open.TryGetValue(Key(engine, vm), out var current) || current.Session != session) return;
            await CloseAsync(Key(engine, vm)).ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }

    /// <summary>The port forwarded for the VM's sign-in now, or null.</summary>
    public int? Port(string engine, string vm)
    {
        lock (_open) return _open.TryGetValue(Key(engine, vm), out var forward) ? forward.Port : null;
    }

    /// <summary>The engine's word on the sign-in: its link and loopback port while it runs, else null.</summary>
    private async Task<(string Url, int Port)?> RedirectAsync(string engine, string vm, SignInForwardRequest request, CancellationToken ct)
    {
        JsonObject? read;
        try
        {
            read = await engines.GetAsync(engine, $"vms/{Uri.EscapeDataString(vm)}/agents/{Uri.EscapeDataString(request.Agent)}/sign-in", ct)
                .ConfigureAwait(false) as JsonObject;
        }
        catch (AgentFailure ex) when (ex.Status == 404)
        {
            return null;
        }

        if (read?["id"]?.GetValue<string>() != request.Session || read["state"]?.GetValue<string>() != "running") return null;
        if (read["browser"] is not JsonObject browser || browser["url"]?.GetValue<string>() is not { } url || browser["port"]?.GetValue<int>() is not int port)
            return null;
        // The link must be the web page it says and send the browser back to that port here (1024-65535).
        return port is >= 1024 and <= 65535 && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
            ? (url, port) : null;
    }

    /// <summary>Closes the forward once the sign-in is over, or after <see cref="Limit"/>.</summary>
    private async Task WatchAsync(string engine, string vm, SignInForwardRequest request, Forward forward)
    {
        var until = DateTime.UtcNow + Limit;
        try
        {
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(Poll, forward.Stop.Token).ConfigureAwait(false);
                try
                {
                    if (await RedirectAsync(engine, vm, request, forward.Stop.Token).ConfigureAwait(false) is null) break;
                }
                catch (AgentFailure)
                {
                    // An engine that does not answer: the limit ends it.
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_open)
                if (!_open.TryGetValue(Key(engine, vm), out var current) || current != forward) return;
            await CloseAsync(Key(engine, vm)).ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task CloseAsync(string key)
    {
        Forward? forward;
        lock (_open)
            if (_open.Remove(key, out forward)) { }
        if (forward is null) return;
        await forward.Stop.CancelAsync().ConfigureAwait(false);
        await forward.Tunnel.DisposeAsync().ConfigureAwait(false);
    }

    private static string Key(string engine, string vm) => VmLinks.Key(engine, vm.ToLowerInvariant());

    public async ValueTask DisposeAsync()
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            string[] keys;
            lock (_open) keys = [.. _open.Keys];
            foreach (var key in keys) await CloseAsync(key).ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    private sealed class Forward(string session, int port, IAsyncDisposable tunnel)
    {
        public string Session { get; } = session;
        public int Port { get; } = port;
        public IAsyncDisposable Tunnel { get; } = tunnel;
        public CancellationTokenSource Stop { get; } = new();
        public Task Watch { get; set; } = Task.CompletedTask;
    }
}
