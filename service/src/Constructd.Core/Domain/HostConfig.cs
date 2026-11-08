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
/// <summary>
/// The host's <c>backups</c> configuration section (docs/plans/host-config-backups.md §3.6).
/// <c>IntervalHours</c> 0 turns scheduled backups off on this host; manual and pre-reinstall
/// backups still work while <c>Enabled</c>. <c>MaxBytesPerVm</c> null means no per-VM quota.
/// </summary>
public sealed record BackupsConfig(bool Enabled = true, int IntervalHours = 24, int Keep = 4, int MaxKeep = 10,
    int PinDays = 14, int RetainDeletedDays = 14, long MinFreeBytes = 5L << 30, long? MaxBytesPerVm = null);
public sealed record MaintenanceMarker(MaintenanceState State, string? UpdateId, DateTimeOffset Since);
