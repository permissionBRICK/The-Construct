using Constructd.Core.Abstractions;
namespace Constructd.Core.Services;

public sealed class InMemoryOperationRegistry : IOperationRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string Kind, string? Vm)> _alive = new(StringComparer.Ordinal);
    public IDisposable Register(string operationId, string kind, string? vmName)
    {
        lock (_gate)
        {
            if (!_alive.TryAdd(operationId, (kind, vmName))) throw new InvalidOperationException("Operation is already registered.");
            return new Handle(() => { lock (_gate) _alive.Remove(operationId); });
        }
    }
    public bool IsAlive(string operationId) { lock (_gate) return _alive.ContainsKey(operationId); }
    public IReadOnlyList<(string OperationId, string Kind, string? VmName)> Alive()
    { lock (_gate) return _alive.Select(x => (x.Key, x.Value.Kind, x.Value.Vm)).ToArray(); }
    private sealed class Handle(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
