namespace Constructd.Core.Domain;

public sealed record CapacityConfig(CapacityMode Mode, long? RamHeadroomBytes, long StorageHeadroomBytes, int? CpuBudget, int? MaxVcpusPerVm, int ReconcileSeconds, int OrphanReservationTimeoutSeconds);
/// <summary>Host-wide HARD ceilings applied after user resolution (null = no cap).</summary>
public sealed record UserCapsConfig(int? MaxRetainedChildren, int? CpuBudget, long? RamBudgetBytes, long? StorageBudgetBytes, long? MaxChildLifetimeSeconds, bool? AllowNeverLifetime, bool? AllowSharing);
public sealed record UserDefaultsConfig(int MaxPrimaries, bool AllowChildCreation, int MaxRetainedChildren, int? CpuBudget, long? RamBudgetBytes, long? StorageBudgetBytes, long? MaxChildLifetimeSeconds, bool AllowNeverLifetime, bool AllowSharing);
public sealed record LifecycleConfig(int GracefulShutdownTimeoutSeconds, int LeaseTickSeconds, int LeaseRetrySeconds);
public sealed record MediaConfig(long MaxBytes, int MaxItemsPerUser, int UploadChunkBytes, int UploadTtlHours, int AcquireTimeoutMinutes, bool AllowHttp, int? UnreferencedTtlHours);
public sealed record NetworkConfig(bool HostForwardsEnabled, bool DirectAddressReporting);
public sealed record UpdatesConfig(string Repository, string Channel, int DrainTimeoutMinutes, int HealthTimeoutSeconds);
public sealed record MaintenanceMarker(MaintenanceState State, string? UpdateId, DateTimeOffset Since);
