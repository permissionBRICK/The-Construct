using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

/// <summary>Why an <see cref="IVmRepository.AddAsync"/> did or did not take.</summary>
public enum VmAddOutcome
{
    Added,
    NameTaken,
    QuotaExceeded,
}

/// <summary>
/// The VM registry — the canonical state for remote VMs (plan §3.3). Also holds the latest
/// activity heartbeat per VM, which the idle engine reads.
/// </summary>
public interface IVmRepository
{
    Task<Vm?> GetAsync(string name, CancellationToken cancellationToken);

    /// <summary>All VMs, or only those of <paramref name="owner"/> when it is not null.</summary>
    Task<IReadOnlyList<Vm>> ListAsync(string? owner, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a VM only if its name is free AND its owner is still below <paramref name="maxVms"/>.
    /// Both checks and the insert happen atomically, so concurrent <c>POST /vms</c> calls cannot race
    /// past the quota (the durable implementation does this in one transaction).
    /// </summary>
    Task<VmAddOutcome> AddAsync(Vm vm, int maxVms, CancellationToken cancellationToken);

    /// <summary>Returns false when the VM does not exist.</summary>
    Task<bool> UpdateAsync(Vm vm, CancellationToken cancellationToken);

    /// <summary>Returns false when the VM does not exist.</summary>
    Task<bool> RemoveAsync(string name, CancellationToken cancellationToken);

    /// <summary>How many VMs a user owns (quota reporting, user deletion).</summary>
    Task<int> CountByOwnerAsync(string owner, CancellationToken cancellationToken);

    Task SaveActivityAsync(ActivityReport report, CancellationToken cancellationToken);

    Task<ActivityReport?> GetLatestActivityAsync(string vmName, CancellationToken cancellationToken);
}
