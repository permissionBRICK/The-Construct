namespace Constructd.Core.Abstractions;

/// <summary>Outcome of moving a VM's virtual hard disk to a destination directory.</summary>
/// <param name="OldDiskPath">Previous path of the VM's primary disk.</param>
/// <param name="NewDiskPath">New path of the VM's primary disk (same as <paramref name="OldDiskPath"/> when <paramref name="Moved"/> is false).</param>
/// <param name="NewVolume">Normalized volume root (e.g. <c>D:\</c>) holding <paramref name="NewDiskPath"/>.</param>
/// <param name="Moved">False when the disk was already inside the requested destination directory.</param>
public sealed record VmStorageMoveResult(string OldDiskPath, string NewDiskPath, string NewVolume, bool Moved);

/// <summary>
/// Hypervisor capability for relocating an existing VM's virtual hard disk to another host folder (Hyper-V <c>Move-VMStorage</c>).
/// Registered only on platforms that support host-path disk relocation.
/// </summary>
public interface IVmStorageDriver
{
    Task<VmStorageMoveResult> MoveStorageAsync(string name, string destinationDirectory, IProgress<string>? progress, CancellationToken ct);
}
