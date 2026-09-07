using System.Collections.Concurrent;
using Constructd.Core.Abstractions;
namespace Constructd.Core.Services;

/// <summary>Shared case-insensitive gate. Entries are retained so waiters never split across locks.</summary>
public sealed class InMemoryVmOperationGate : IVmOperationGate
{
    private sealed class Entry { public readonly SemaphoreSlim Gate = new(1, 1); public string? Owner; }
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    public async Task<IAsyncDisposable> AcquireAsync(string vmName, string operationId, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd(vmName, _ => new());
        await entry.Gate.WaitAsync(ct); Volatile.Write(ref entry.Owner, operationId);
        return new Handle(entry);
    }
    public async Task<IAsyncDisposable?> TryAcquireAsync(string vmName, string operationId, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd(vmName, _ => new());
        if (!await entry.Gate.WaitAsync(0, ct)) return null;
        Volatile.Write(ref entry.Owner, operationId); return new Handle(entry);
    }
    public bool IsHeld(string vmName, out string? operationId)
    {
        operationId = _entries.TryGetValue(vmName, out var entry) ? Volatile.Read(ref entry.Owner) : null;
        return operationId is not null;
    }
    private sealed class Handle(Entry entry) : IAsyncDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) { Volatile.Write(ref entry.Owner, null); entry.Gate.Release(); }
            return ValueTask.CompletedTask;
        }
    }
}
