using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Fakes;

// Scripts are matched exactly, never interpreted as shell. Tests program spool transitions
// with ExpectRun, then drive the same watch/output lifecycle as the real transport.
public sealed class ScriptedGuestSpool
{
    private readonly Queue<(string Script, Func<ScriptedGuestSpool, ProcessResult> Respond)> steps = new();
    public Dictionary<string, string> Requests { get; } = [];
    public Dictionary<string, string> Acks { get; } = [];
    public Dictionary<string, string> Closes { get; } = [];
    public Queue<string> Notifications { get; } = new();
    public int RemainingSteps => steps.Count;
    public void ExpectRun(string script, Func<ScriptedGuestSpool, ProcessResult> respond) => steps.Enqueue((script, respond));
    public void ExpectRun(string script, ProcessResult result) => ExpectRun(script, _ => result);
    public ProcessResult Run(string script)
    {
        if (!steps.TryDequeue(out var step) || step.Script != script)
            throw new InvalidOperationException("Unexpected guest script.");
        return step.Respond(this);
    }
    public void SignalForwardChange(FakeRunningProcess watch) => watch.Emit("CHANGED\n");
    public void DrainNotifications(FakeRunningProcess watch)
    {
        while (Notifications.TryDequeue(out var message)) watch.Emit(message + "\n");
    }
}

public sealed class FakeSshTransport : ISshTransport
{
    public Func<string, CancellationToken, Task<ProcessResult>>? ScriptHandler { get; set; }
    public Action<FakeRunningProcess>? TunnelStarted { get; set; }
    public ScriptedGuestSpool Spool { get; } = new();
    public List<string> Scripts { get; } = [];
    public List<TimeSpan?> ScriptTimeouts { get; } = [];
    public List<(string Script, FakeRunningProcess Process)> Watches { get; } = [];
    public List<(TunnelSpec Spec, FakeRunningProcess Process)> Tunnels { get; } = [];
    public HashSet<(int Port, string BindHost)> BusyPorts { get; } = [];
    public Task<ProcessResult> RunRemoteScriptAsync(string script, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Scripts.Add(script); ScriptTimeouts.Add(timeout); return ScriptHandler is null ? Task.FromResult(Spool.Run(script)) : ScriptHandler(script, cancellationToken);
    }
    public IRunningProcess SpawnWatch(string script, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var child = new FakeRunningProcess(cancellationToken); Watches.Add((script, child)); return child;
    }
    public IRunningProcess SpawnTunnel(TunnelSpec tunnel, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var child = new FakeRunningProcess(cancellationToken); Tunnels.Add((tunnel, child)); TunnelStarted?.Invoke(child); return child;
    }
    public Task<bool> ProbePortAsync(int port, string bindHost = "127.0.0.1", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(!BusyPorts.Contains((port, bindHost)));
    }
}
