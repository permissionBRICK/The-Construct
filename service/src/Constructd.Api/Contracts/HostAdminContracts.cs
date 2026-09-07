using Constructd.Core.Domain;
namespace Constructd.Api.Contracts;

public sealed record EffectiveAllowanceResponse(int MaxPrimaries, bool AllowChildCreation, int MaxRetainedChildren,
    int? CpuBudget, long? RamBudgetBytes, long? StorageBudgetBytes, long? MaxChildLifetimeSeconds,
    bool AllowNeverLifetime, bool AllowSharing, bool AllowHostForwards, AllowanceUsage Usage)
{
    public static EffectiveAllowanceResponse From(EffectiveAllowance a, AllowanceUsage usage) => new(a.MaxPrimaries, a.AllowChildCreation,
        a.MaxRetainedChildren, a.CpuBudget, a.RamBudgetBytes, a.StorageBudgetBytes, a.MaxChildLifetimeSeconds, a.AllowNeverLifetime, a.AllowSharing, a.AllowHostForwards, usage);
}
public sealed record UserDetailResponse(string Name, Role Role, bool Enabled, int MaxVms, bool AllowHostForwards, DateTimeOffset Created,
    UserAllowance Allowance, EffectiveAllowanceResponse Effective, UserVmCounts Vms, int Tokens);
public sealed record UserVmCounts(int Primaries, int Children);
public sealed record UserUpdateRequest(string? Role = null, bool? Enabled = null, int? MaxVms = null, bool? AllowHostForwards = null);
public sealed record VmOverrideRequest(bool? AllowChildCreation = null, int? MaxRetainedChildren = null, long? MaxChildLifetimeSeconds = null,
    bool? AllowNeverLifetime = null, bool? AllowSharing = null);
public sealed record VmTokenRequest(string? Kind = null);
public sealed record GuestReportRequest(string? Event, string? Outcome, string? ConstructCommit, DateTimeOffset? At, string? Reporter);
public sealed record LeaseResponse(string Requested, DateTimeOffset? ActivatedAt, DateTimeOffset? ExpiresAt, LeaseState State,
    bool Overdue, long Version, DateTimeOffset? LastAttemptAt, string? LastOutcome);
public sealed record VmReservationsResponse(long RamBytes, int Cpus, long StorageBytes);
public sealed record CurrentOperationResponse(string JobId, string Kind, string? Phase, string? Initiator);
