namespace Constructd.Core.Abstractions;

/// <summary>
/// The RAM a primary VM may be shrunk to while it runs (Proxmox auto-ballooning: the node inflates the
/// guest's balloon when host memory use passes its ballooning target and deflates it again when there is room).
/// </summary>
public interface IVmMemoryFloorDriver
{
    /// <summary>
    /// Sets the VM's balloon minimum to <paramref name="minimumMb"/> MiB, or removes it when null or not below
    /// the VM's configured RAM. Works on a running VM without shrinking it; idempotent.
    /// </summary>
    Task SetMemoryFloorAsync(string name, int? minimumMb, CancellationToken ct);
}
