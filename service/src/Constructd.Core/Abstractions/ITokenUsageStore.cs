using Constructd.Core.Domain;
namespace Constructd.Core.Abstractions;

public interface ITokenUsageStore
{
    // False when the VM disappeared, changed incarnation/owner, or entered deletion.
    Task<bool> UpsertAsync(Vm vm, IReadOnlyList<TokenUsageDay> days, DateTimeOffset reportedAt, CancellationToken ct);
    Task<IReadOnlyList<TokenUsageRow>> ListAsync(string? owner, string? vm, CancellationToken ct);
    Task<int> PruneAsync(DateOnly cutoff, CancellationToken ct);
}
