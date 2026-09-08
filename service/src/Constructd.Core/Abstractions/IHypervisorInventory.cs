using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

public sealed record HypervisorDiskInfo(string Path, long MaxBytes, long FileBytes, string? ParentPath, string Volume, bool Readable);
public sealed record HypervisorVmInfo(
    string Name,
    string Id,
    VmState State,
    string RawState,
    int Generation,
    int Cpus,
    long MemoryStartupBytes,
    long MemoryAssignedBytes,
    bool DynamicMemory,
    long? MemoryMaximumBytes,
    IReadOnlyList<HypervisorDiskInfo> Disks,
    long? SavedStateBytes,
    string ConfigVolume,
    bool Complete,
    double? CpuUsagePercent = null, long? MemoryDemandBytes = null, double? UptimeSeconds = null);
public sealed record VolumeInfo(string Root, long TotalBytes, long FreeBytes);
public sealed record HostResourcesInfo(int LogicalCpus, long TotalRamBytes, long FreeRamBytes, IReadOnlyList<VolumeInfo> Volumes, DateTimeOffset ObservedAt);
/// <summary>ONE epoch: VMs, host resources and volume free space read in the same pass; Complete=false ⇒ admission fails closed.</summary>
public enum ArtifactPresence { Unknown, Absent, Present }
/// <summary>Evidence collected in the same pass as volume free space. Key is the reservation artifact.</summary>
public sealed record CapacityArtifactInfo(string Artifact, string? Path, string Volume, long FileBytes, ArtifactPresence Presence);
public sealed record InventorySnapshot(long Epoch, DateTimeOffset ObservedAt, HostResourcesInfo Host, IReadOnlyList<HypervisorVmInfo> Vms, bool Complete, IReadOnlyList<string> Problems,
    IReadOnlyList<CapacityArtifactInfo>? Artifacts = null);

public interface IHypervisorInventory
{
    Task<InventorySnapshot> ReadAsync(CancellationToken ct);
    /// <summary>Includes retained/unattached artifacts; implementations without evidence retain them conservatively.</summary>
    Task<InventorySnapshot> ReadAsync(IReadOnlyList<Reservation> reservations, CancellationToken ct) => ReadAsync(ct);
}
