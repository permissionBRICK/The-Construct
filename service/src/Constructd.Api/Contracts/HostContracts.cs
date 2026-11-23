using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Services;

namespace Constructd.Api.Contracts;

/// <summary>The host's platform (<c>Constructd:Backend</c>): Hyper-V, a Proxmox VE node, or VMs that exist elsewhere.</summary>
public enum HostKind { Hyperv, Proxmox, External }

// ── GET /host/status. The records write exactly what the anonymous objects wrote before. ──

public sealed record HostStatusResponse(InstalledRelease Version, HostHealthResponse Health, HostCapacitySummary Capacity, CapacityMode CapacityMode,
    MemoryPressureStatus MemoryPressure, HostMaintenanceResponse Maintenance, IReadOnlyList<HostActiveJobResponse> ActiveJobs,
    int LeaseOverdueCount, int UnmanagedVmCount);

/// <param name="Hypervisor"><c>ok</c> or <c>unreachable</c>.</param>
/// <param name="Database">Always <c>ok</c> when the service answers.</param>
/// <param name="Media"><c>ok</c> or <c>missing-root</c>.</param>
/// <param name="Inventory"><c>complete</c> or <c>incomplete</c>.</param>
public sealed record HostHealthResponse(string Hypervisor, string Database, string Media, string Inventory);

public sealed record HostMaintenanceResponse(MaintenanceState Phase, DateTimeOffset? Since, string? UpdateId);

public sealed record HostActiveJobResponse(string Id, string Kind, string? VmName, string Owner, string? Initiator, string? Phase, DateTimeOffset Created);

/// <summary>The capacity snapshot as <c>/host/status</c> and <c>/host/capacity</c> show it.</summary>
public sealed record HostCapacitySummary(long Epoch, DateTimeOffset ObservedAt, bool Complete, HostRamSummary Ram, HostCpuSummary Cpu,
    IReadOnlyList<VolumeCapacity> Volumes);

/// <param name="HostOwnBytes">RAM the host uses itself (used minus what VMs hold), when both are measured.</param>
/// <param name="CommittedBytes">Reserved plus unmanaged.</param>
public sealed record HostRamSummary(long TotalBytes, long HeadroomBytes, long ReservedBytes, long UnmanagedBytes, long PhysicalFreeBytes,
    long AvailableBytes, long? UsedBytes, long? VmResidentBytes, long? HostOwnBytes, long CommittedBytes, HostRamAdmission Admission, HostSwapSummary? Swap);

public sealed record HostRamAdmission(bool Enforced, long LineBytes, long AvailableBytes);

public sealed record HostSwapSummary(long TotalBytes, long UsedBytes);

public sealed record HostCpuSummary(int Logical, int? Budget, int Active, int? Available);

// ── GET /host/config: described only; the handler writes the sections with their metadata. ──

/// <summary>
/// <c>GET /host/config</c>: every section, its stored value or its default. Each section also carries
/// <c>source</c> (<c>default</c> or <c>stored</c>) and <c>updatedAt</c> next to its own fields (see the schema).
/// </summary>
public sealed record HostConfigResponse(CapacityConfig Capacity, UsageConfig Usage, MemoryPressureConfig MemoryPressure,
    UserDefaultsConfig UserDefaults, UserCapsConfig UserCaps, LifecycleConfig Lifecycle, MediaConfig Media, NetworkConfig Network,
    VirtualizationConfig Virtualization, UpdatesConfig Updates, VaultConfig Vault, HttpsConfig Https, BackupsConfig Backups, RepairConfig Repair);

// ── GET /host/windows ──

public sealed record HostWindowsResponse(IReadOnlyList<WindowsKeyInfo> Keys, IReadOnlyList<WindowsGuestStatus> Guests,
    IReadOnlyList<WindowsLicenseMachine> Machines);

// ── the update history in GET /host/updates/status ──

/// <summary>One host update as the status shows it (<c>current</c> and each <c>history</c> row).</summary>
public sealed record HostUpdateHistoryEntry(string UpdateId, string Commit, HostUpdateState State, string? Phase,
    IReadOnlyList<HostUpdatePhase> Phases, DateTimeOffset Started, DateTimeOffset? Finished, string? Error, IReadOnlyList<string> BlockingJobs);

// ── GET /host/iso-catalog ──

public sealed record IsoCatalogResponse(IsoBuildMode Mode, IsoCatalogSource Source, IsoCatalogCurrent? Current, IReadOnlyList<IsoCatalogItem> Entries);

/// <param name="Url">The configured source URL without credentials, query or fragment.</param>
public sealed record IsoCatalogSource(string? Path, string? Url, bool Sha256Configured, bool Present, long? SizeBytes);

public sealed record IsoCatalogCurrent(string FileName, long SizeBytes, DateTimeOffset? BuiltAt, string? SourceSha256,
    string? BootstrapKeyFingerprint, string? HostnameSource);

public sealed record IsoCatalogItem(string FileName, long SizeBytes, bool IsCurrent, DateTimeOffset? BuiltAt, bool SidecarReadable);
