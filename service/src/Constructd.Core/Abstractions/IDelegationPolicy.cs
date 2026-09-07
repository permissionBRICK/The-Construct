using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

public interface IDelegationPolicy
{
    /// <summary>§1.1: resolve(user field ?? userDefaults) → cap by userCaps → restrict by override(parent); an override only tightens.</summary>
    Task<EffectiveAllowance> ResolveAsync(string owner, string? parentVm, CancellationToken ct);
    Task<AllowanceUsage> UsageAsync(string owner, CancellationToken ct);
    Task<IReadOnlyList<ChildAction>> AllowedActionsAsync(Vm vm, string principal, ForwardRelationship relationship, CancellationToken ct);
}
