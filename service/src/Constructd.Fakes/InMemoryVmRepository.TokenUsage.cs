using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Fakes;

public sealed partial class InMemoryVmRepository : ITokenUsageStore
{
    private readonly Dictionary<(string Vm, string Tool, string Day), TokenUsageRow> tokenUsage = [];
    public Task<bool> UpsertAsync(Vm vm, IReadOnlyList<TokenUsageDay> days, DateTimeOffset reportedAt, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            ct.ThrowIfCancellationRequested();
            if (!_vms.TryGetValue(vm.Name, out var current) || current.Deleting || current.Incarnation != vm.Incarnation ||
                !Ownership.SameName(current.Owner, vm.Owner)) return Task.FromResult(false);
            foreach (var day in days) tokenUsage[(vm.Name.ToUpperInvariant(), day.Tool, day.Day)] = new(vm.Name, vm.Owner, day, reportedAt);
            return Task.FromResult(true);
        }
    }
    public Task<IReadOnlyList<TokenUsageRow>> ListAsync(string? owner, string? vm, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<TokenUsageRow>>(tokenUsage.Values.Where(r =>
                (owner is null || Ownership.SameName(owner, r.Owner)) && (vm is null || Ownership.SameName(vm, r.Vm))).ToArray());
        }
    }
    public Task<int> PruneAsync(DateOnly cutoff, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            ct.ThrowIfCancellationRequested();
            var keys = tokenUsage.Where(r => TokenUsageMath.PeriodEnd(r.Value.Usage.Day) < cutoff).Select(r => r.Key).ToArray();
            foreach (var key in keys) tokenUsage.Remove(key);
            return Task.FromResult(keys.Length);
        }
    }
    private void MarkUsageDeleted(string name)
    {
        foreach (var key in tokenUsage.Keys.Where(k => Ownership.SameName(k.Vm, name)).ToArray())
            tokenUsage[key] = tokenUsage[key] with { VmDeletedAt = tokenUsage[key].VmDeletedAt ?? clock?.UtcNow ?? DateTimeOffset.UtcNow };
    }
}
