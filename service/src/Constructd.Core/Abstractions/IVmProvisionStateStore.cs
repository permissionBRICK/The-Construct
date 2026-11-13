using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

/// <summary>
/// Where a VM's desired state comes from (design 7.5, the <c>vm-desired</c> record). Today the engine's
/// own database, written by the last provision request; the synced records of a later phase can back
/// the same interface.
/// </summary>
public interface IVmDesiredStateSource
{
    Task<StoredDesiredState?> GetDesiredAsync(string vmName, CancellationToken ct);

    Task SetDesiredAsync(string vmName, StoredDesiredState desired, CancellationToken ct);
}

public static class VmDesiredStateSourceExtensions
{
    /// <summary>The stored settings of this VM, or null when none are stored or they belong to an earlier VM of the name.</summary>
    public static async Task<StoredDesiredState?> GetDesiredForAsync(this IVmDesiredStateSource source, Vm vm, CancellationToken ct) =>
        await source.GetDesiredAsync(vm.Name, ct) is { } stored && (stored.VmCreated is null || stored.VmCreated == vm.Created) ? stored : null;
}

/// <summary>What the guest reported after the last provision run (the <c>vm-applied</c> record).</summary>
public interface IVmAppliedStateStore
{
    Task<VmAppliedState?> GetAppliedAsync(string vmName, CancellationToken ct);

    Task SetAppliedAsync(string vmName, VmAppliedState applied, CancellationToken ct);
}
