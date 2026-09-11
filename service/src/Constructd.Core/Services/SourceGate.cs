using System.Collections.Concurrent;
namespace Constructd.Core.Services;

/// <summary>Entries stay allocated so existing waiters can never split across gates.</summary>
public sealed class SourceGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);
    public async Task<IDisposable> AcquireAsync(string commit, CancellationToken ct)
    { var gate = gates.GetOrAdd(commit, _ => new(1, 1)); await gate.WaitAsync(ct); return new Handle(gate); }
    public async Task<IDisposable?> TryAcquireAsync(string commit, CancellationToken ct)
    { var gate = gates.GetOrAdd(commit, _ => new(1, 1)); return await gate.WaitAsync(TimeSpan.FromSeconds(1), ct) ? new Handle(gate) : null; }
    private sealed class Handle(SemaphoreSlim gate) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) gate.Release(); }
    }
}
