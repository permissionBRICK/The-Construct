using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Core.Services;

public sealed class InMemoryMaintenanceGate : IMaintenanceGate
{
    private readonly Lock _gate = new();
    private MaintenanceState _state;
    private readonly Dictionary<string, (string Kind, string? Vm)> _live = new();
    private TaskCompletionSource _changed = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public MaintenanceState State { get { lock (_gate) return _state; } }
    public int LiveHandles { get { lock (_gate) return _live.Count; } }
    public IReadOnlyList<string> BlockingOperations { get { lock (_gate) return _live.Where(x => !x.Value.Kind.StartsWith("mutation:", StringComparison.Ordinal)).Select(x => x.Key).ToArray(); } }
    public IDisposable? TryEnter(string kind, string operationId, string? vmName)
    {
        lock (_gate)
        {
            if (_state == MaintenanceState.Maintenance || (_state == MaintenanceState.Draining && !kind.StartsWith("mutation:", StringComparison.Ordinal))) return null;
            if (!_live.TryAdd(operationId, (kind, vmName))) throw new InvalidOperationException("Operation already admitted.");
            return new Handle(() => { lock (_gate) { _live.Remove(operationId); Signal(); } });
        }
    }
    public async Task<DrainResult> DrainAsync(TimeSpan timeout, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        lock (_gate) { _state = MaintenanceState.Draining; Signal(); }
        while (true)
        {
            Task changed; TimeSpan remaining;
            lock (_gate)
            {
                if (!_live.Values.Any(v => !v.Kind.StartsWith("mutation:", StringComparison.Ordinal))) return new(true, [], started.Elapsed);
                if (started.Elapsed >= timeout) return new(false, _live.Where(x => !x.Value.Kind.StartsWith("mutation:", StringComparison.Ordinal)).Select(x => (x.Key, x.Value.Kind, x.Value.Vm)).ToArray(), started.Elapsed);
                changed = _changed.Task; remaining = timeout - started.Elapsed;
                if (remaining <= TimeSpan.Zero) continue;
            }
            try { await changed.WaitAsync(remaining, ct); } catch (TimeoutException) { }
        }
    }
    public void Enter(MaintenanceState state, string? updateId) { lock (_gate) { _state = state; Signal(); } }
    public void Reopen() => Enter(MaintenanceState.Open, null);
    private void Signal() { var old = _changed; _changed = NewSignal(); old.TrySetResult(); }
    private sealed class Handle(Action release) : IDisposable
    { private Action? _release = release; public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke(); }
}
