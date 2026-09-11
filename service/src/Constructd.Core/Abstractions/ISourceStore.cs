using Constructd.Core.Domain;
namespace Constructd.Core.Abstractions;

public interface ISourceStore
{
    Task<SourceItem?> GetAsync(string commit, CancellationToken ct);
    Task<IReadOnlyList<SourceItem>> ListAsync(CancellationToken ct);
    Task UpsertAsync(SourceItem item, CancellationToken ct);
    Task<bool> TryTransitionAsync(string commit, SourceState expected, SourceItem updated, CancellationToken ct);
    Task TouchAsync(string commit, DateTimeOffset at, CancellationToken ct);
    Task DeleteAsync(string commit, CancellationToken ct);
    Task<long> CommittedBytesAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> ListPinnedCommitsAsync(CancellationToken ct);
}
