using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

/// <summary>
/// What the hypervisor says about a VM it runs, read only. Adopting a VM the engine did not create
/// (one made by the old client) registers it with these values instead of values a caller states.
/// </summary>
public interface IVmFactsDriver
{
    /// <summary>The VM's facts, or null when the hypervisor has no VM of that name.</summary>
    Task<HypervisorVmFacts?> GetFactsAsync(string name, CancellationToken ct);
}

/// <param name="Id">The hypervisor's own id of the VM (Hyper-V: its GUID).</param>
/// <param name="DiskBytes">Virtual size of every attached disk, summed.</param>
/// <param name="FirmwareId">
/// The id the guest reads as <c>/sys/class/dmi/id/product_uuid</c> (Hyper-V: BIOSGUID), which is how
/// an adoption proves that the guest it reached over SSH is this VM. Null when it cannot be read.
/// </param>
public sealed record HypervisorVmFacts(string Id, VmState State, int Cpu, long MemoryBytes, long DiskBytes, string? FirmwareId);
