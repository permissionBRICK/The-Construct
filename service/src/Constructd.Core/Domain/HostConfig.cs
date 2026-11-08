namespace Constructd.Core.Domain;

public sealed record UsageConfig(int RetentionDays = 400);

public sealed record MemoryPressureConfig(bool Enabled = true, int HighWaterPercent = 90, int LowWaterPercent = 80,
    int SwapHighWaterPercent = 50, int MinSecondsBetweenSaves = 60, int CooldownMinutesAfterSave = 10);
public sealed record CapacityConfig(CapacityMode Mode, long? RamHeadroomBytes, long StorageHeadroomBytes, int? CpuBudget, int? MaxVcpusPerVm, int ReconcileSeconds, int OrphanReservationTimeoutSeconds);
/// <summary>Host-wide HARD ceilings applied after user resolution (null = no cap).</summary>
public sealed record UserCapsConfig(int? MaxRetainedChildren, int? CpuBudget, long? RamBudgetBytes, long? StorageBudgetBytes, long? MaxChildLifetimeSeconds, bool? AllowNeverLifetime, bool? AllowSharing);
public sealed record UserDefaultsConfig(int MaxPrimaries, bool AllowChildCreation, int MaxRetainedChildren, int? CpuBudget, long? RamBudgetBytes, long? StorageBudgetBytes, long? MaxChildLifetimeSeconds, bool AllowNeverLifetime, bool AllowSharing);
public sealed record LifecycleConfig(int GracefulShutdownTimeoutSeconds, int LeaseTickSeconds, int LeaseRetrySeconds);
public sealed record MediaConfig(long MaxBytes, int MaxItemsPerUser, int UploadChunkBytes, int UploadTtlHours, int AcquireTimeoutMinutes, bool AllowHttp, int? UnreferencedTtlHours);
public sealed record NetworkConfig(bool HostForwardsEnabled, bool DirectAddressReporting,
    string DefaultMode = "relayed", bool OwnerMaySwitchMode = false);
/// <summary>VM hardware defaults. <c>MinimumRamBytes</c> (Proxmox): the RAM auto-ballooning may shrink a running
/// primary VM to under host memory pressure; null turns ballooning off. Each VM may override it.</summary>
public sealed record VirtualizationConfig(bool NestedDefault, bool NestedSelectable, long? MinimumRamBytes = 6L << 30);
public sealed record UpdatesConfig(string Repository, string Channel, int DrainTimeoutMinutes, int HealthTimeoutSeconds);
/// <summary>The key vault's host settings. <c>WebUrl</c>: the address phones use for the approval page (null = the service's own).</summary>
public sealed record VaultConfig(string? WebUrl = null);
/// <summary>HTTPS forwards (docs/https-forwards.md). <c>SharedPort</c>: the port (usually 443) on which every VM's
/// main HTTPS forward is served under the VM's own public name, and the vault pages under the vault web address;
/// null turns the shared listener off. <c>StartOnRequest</c>: opening a stopped or saved VM's HTTPS URL starts it.</summary>
public sealed record HttpsConfig(int? SharedPort = null, bool StartOnRequest = true);
public sealed record MaintenanceMarker(MaintenanceState State, string? UpdateId, DateTimeOffset Since);
