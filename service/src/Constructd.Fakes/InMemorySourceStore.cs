using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Fakes;

public sealed class InMemorySourceStore(IVmRepository vms) : ISourceStore
{
    private readonly Dictionary<string, SourceItem> _items = new(StringComparer.Ordinal);
    public Task<SourceItem?> GetAsync(string commit, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); lock (InMemoryTransaction.Gate) return Task.FromResult(_items.GetValueOrDefault(commit)); }
    public Task<IReadOnlyList<SourceItem>> ListAsync(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); lock (InMemoryTransaction.Gate) return Task.FromResult<IReadOnlyList<SourceItem>>(_items.Values.OrderBy(i => i.Commit).ToArray()); }
    public Task UpsertAsync(SourceItem item, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); lock (InMemoryTransaction.Gate) _items[item.Commit] = item; return Task.CompletedTask; }
    public Task<bool> TryTransitionAsync(string commit, SourceState expected, SourceItem updated, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); lock (InMemoryTransaction.Gate)
        {
            if (commit != updated.Commit || !_items.TryGetValue(commit, out var item) || item.State != expected) return Task.FromResult(false);
            _items[commit] = updated; return Task.FromResult(true);
        }
    }
    public Task TouchAsync(string commit, DateTimeOffset at, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); lock (InMemoryTransaction.Gate) if (_items.TryGetValue(commit, out var item)) _items[commit] = item with { LastUsedAt = at }; return Task.CompletedTask; }
    public Task DeleteAsync(string commit, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); lock (InMemoryTransaction.Gate) _items.Remove(commit); return Task.CompletedTask; }
    public Task<long> CommittedBytesAsync(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); lock (InMemoryTransaction.Gate) return Task.FromResult(_items.Values.Where(i => i.State != SourceState.Failed).Sum(i => i.SizeBytes)); }
    public async Task<IReadOnlyList<string>> ListPinnedCommitsAsync(CancellationToken ct) =>
        (await vms.ListAsync(null, ct)).SelectMany(v => new[] { v.SourceCommit, v.Guest?.ConstructCommit }).OfType<string>().Distinct().ToArray();
}
