using System.Collections.Concurrent;
using Constructd.Core.Abstractions;
namespace Constructd.Fakes;

public sealed class FakeHostLock : IHostLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    public bool HeldByAnotherProcess { get; set; }
    public bool IsHeldByAnotherProcess(string name) => HeldByAnotherProcess;
    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan wait, CancellationToken ct)
    {
        if (HeldByAnotherProcess) return null;
        var gate = _locks.GetOrAdd(name, _ => new(1, 1));
        return await gate.WaitAsync(wait, ct) ? new Handle(gate) : null;
    }
    private sealed class Handle(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Release(); return ValueTask.CompletedTask; }
    }
}
