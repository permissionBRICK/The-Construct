namespace Constructd.Core.Domain;

/// <summary>Stored per user; every field null = "use host userDefaults".</summary>
public sealed record UserAllowance(
    bool? AllowChildCreation,
    int? MaxRetainedChildren,
    int? CpuBudget,
    long? RamBudgetBytes,
    long? StorageBudgetBytes,
    long? MaxChildLifetimeSeconds,
    bool? AllowNeverLifetime,
    bool? AllowSharing)
{
    public static UserAllowance Unset { get; } = new(null, null, null, null, null, null, null, null);
}

/// <summary>Per-primary restriction (restrict-only: the effective value is the minimum of override and user value).</summary>
public sealed record VmOverride(
    string VmName,
    bool? AllowChildCreation,
    int? MaxRetainedChildren,
    long? MaxChildLifetimeSeconds,
    bool? AllowNeverLifetime,
    bool? AllowSharing,
    DateTimeOffset UpdatedAt);

/// <summary>Resolved, non-nullable view for one owner (+ optional parent override).</summary>
public sealed record EffectiveAllowance(
    int MaxPrimaries,
    bool AllowChildCreation,
    int MaxRetainedChildren,
    int? CpuBudget,
    long? RamBudgetBytes,
    long? StorageBudgetBytes,
    long? MaxChildLifetimeSeconds,
    bool AllowNeverLifetime,
    bool AllowSharing,
    bool AllowHostForwards);

public sealed record AllowanceUsage(int Primaries, int Children, int Cpus, long RamBytes, long StorageBytes);
