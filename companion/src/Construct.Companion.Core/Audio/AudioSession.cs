using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Core.Audio;

public sealed record AudioStatus(bool Enabled = false, bool Capturing = false, string? Tunnel = null, bool? GatePatched = null, string? Error = null)
{
    public System.Text.Json.Nodes.JsonObject ToMessage(string instance)
    {
        var message = new System.Text.Json.Nodes.JsonObject { ["type"] = "audio", ["instance"] = instance, ["enabled"] = Enabled, ["capturing"] = Capturing };
        if (!string.IsNullOrEmpty(Tunnel)) message["tunnel"] = Tunnel;
        if (GatePatched is bool patched) message["gatePatched"] = patched;
        if (!string.IsNullOrEmpty(Error)) message["error"] = Error;
        return message;
    }
}

public sealed class AudioSession(ISshTransport ssh, IRuntimeProcesses processes, IAudioServerFactory servers,
    SharedAudioCapture capture, Action<AudioStatus>? changed = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim serial = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? sessionStop;
    private IAudioServer? server;
    private ISupervisedProcess? tunnel;
    private Task accepts = Task.CompletedTask, monitor = Task.CompletedTask;
    private readonly List<Task> connections = [];
    private AudioStatus state = new();
    private int openConnections;
    private int selfPort;
    private bool remoteEnabled;
    public AudioStatus Status => Volatile.Read(ref state);
    private void Publish(AudioStatus status)
    { Volatile.Write(ref state, status); try { changed?.Invoke(status); } catch { } }
    private void Update(Func<AudioStatus, AudioStatus> update)
    {
        AudioStatus before, next;
        do { before = Status; next = update(before); } while (!ReferenceEquals(Interlocked.CompareExchange(ref state, next, before), before));
        try { changed?.Invoke(next); } catch { }
    }
    public void MarkGatePatched() => Update(current => current with { GatePatched = true });
    public async Task<bool> EnableAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        await serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (Status.Enabled) return true;
            await TeardownAsync().ConfigureAwait(false);
            var response = await ssh.RunRemoteScriptAsync(AudioProtocol.EnableScript(), TimeSpan.FromSeconds(60), linked.Token).ConfigureAwait(false);
            if (response.Code != 0) { Publish(new(Error: response.Code < 0 ? "unreachable" : "enable-failed")); return false; }
            remoteEnabled = true;
            linked.Token.ThrowIfCancellationRequested();
            var gatePatched = AudioProtocol.ConfirmPatched("CONSTRUCT_GATE_PATCHED", response.Stdout);
            sessionStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            server = await servers.ListenAsync(linked.Token).ConfigureAwait(false); linked.Token.ThrowIfCancellationRequested();
            var candidates = AudioProtocol.PortCandidates(AudioProtocol.ParseBusyPorts(response.Stdout));
            foreach (var port in candidates)
            {
                linked.Token.ThrowIfCancellationRequested();
                var spec = new TunnelSpec(server.Port, port, Direction: TunnelDirection.Reverse);
                tunnel = processes.Start(ct => ssh.SpawnTunnel(spec, ct), new(TimeSpan.FromMilliseconds(1200), Restart: false), _ => { });
                if (await tunnel.FirstAttempt.WaitAsync(linked.Token).ConfigureAwait(false) && tunnel.State.State == "up") { selfPort = port; break; }
                await tunnel.DisposeAsync().ConfigureAwait(false); tunnel = null;
            }
            if (tunnel is null) { await RollbackAsync().ConfigureAwait(false); Publish(new(Error: candidates.Length == 0 ? "no-free-port" : "tunnel-failed")); return false; }
            linked.Token.ThrowIfCancellationRequested();
            Publish(new(true, false, $"vm:{selfPort} → host mic (:{server.Port})", gatePatched));
            accepts = AcceptAsync(server, tunnel, sessionStop);
            monitor = MonitorTunnelAsync(tunnel, server, sessionStop);
            return true;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { await RollbackAsync().ConfigureAwait(false); Publish(new(Error: "disposed")); return false; }
        catch { await RollbackAsync().ConfigureAwait(false); Publish(new(Error: "enable-failed")); return false; }
        finally { serial.Release(); }
    }
    private async Task AcceptAsync(IAudioServer listener, ISupervisedProcess process, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await foreach (var connection in listener.AcceptAsync(token).WithCancellation(token).ConfigureAwait(false))
            { lock (connections) { connections.RemoveAll(t => t.IsCompleted); connections.Add(ServeAsync(connection, token)); } }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            Update(current => current with { Enabled = false, Capturing = false, Tunnel = null, Error = "server-failed" });
            await cancellation.CancelAsync().ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }
    private async Task ServeAsync(IAudioConnection connection, CancellationToken token)
    {
        await Task.Yield();
        try
        {
            if (connection.Closed.IsCompleted) return;
            token.ThrowIfCancellationRequested();
            await using var subscription = await capture.AttachAsync(connection, token).ConfigureAwait(false);
            Interlocked.Increment(ref openConnections); Update(current => current with { Capturing = true });
            try { await connection.Closed.WaitAsync(token).ConfigureAwait(false); }
            finally { if (Interlocked.Decrement(ref openConnections) == 0) Update(current => current with { Capturing = false }); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { Update(current => current with { Error = "capture-failed" }); }
        finally { await connection.DisposeAsync().ConfigureAwait(false); }
    }
    private async Task MonitorTunnelAsync(ISupervisedProcess process, IAudioServer listener, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        // Completion is the seam's terminal task; no polling or unowned task survives teardown.
        try
        {
            await process.Completion.WaitAsync(token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            Update(current => current with { Enabled = false, Capturing = false, Tunnel = null, Error = "tunnel-down" });
            await cancellation.CancelAsync().ConfigureAwait(false);
            await listener.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private async Task TeardownAsync()
    {
        if (sessionStop is not null) await sessionStop.CancelAsync().ConfigureAwait(false);
        if (tunnel is not null) await tunnel.DisposeAsync().ConfigureAwait(false); tunnel = null;
        if (server is not null) await server.DisposeAsync().ConfigureAwait(false); server = null;
        await Task.WhenAll(accepts, monitor).ConfigureAwait(false);
        Task[] pending; lock (connections) { pending = connections.ToArray(); connections.Clear(); }
        await Task.WhenAll(pending).ConfigureAwait(false); sessionStop?.Dispose(); sessionStop = null;
    }
    private async Task RollbackAsync()
    {
        await TeardownAsync().ConfigureAwait(false);
        if (remoteEnabled)
        {
            try { await ssh.RunRemoteScriptAsync(AudioProtocol.DisableScript(selfPort), TimeSpan.FromSeconds(60), CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }
        remoteEnabled = false; selfPort = 0;
    }
    public async Task<bool> DisableAsync(CancellationToken cancellationToken = default)
    {
        await serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TeardownAsync().ConfigureAwait(false);
            var result = await ssh.RunRemoteScriptAsync(AudioProtocol.DisableScript(selfPort), TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            remoteEnabled = false; selfPort = 0; Publish(new()); return result.Code == 0;
        }
        catch { Publish(new(Error: "disable-failed")); return false; }
        finally { serial.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await serial.WaitAsync().ConfigureAwait(false);
        try { await TeardownAsync().ConfigureAwait(false); Publish(new()); }
        finally { serial.Release(); }
    }
}
