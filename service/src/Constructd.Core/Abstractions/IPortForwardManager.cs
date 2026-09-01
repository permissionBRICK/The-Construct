using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Core.Abstractions;

/// <summary>
/// Owns the service host's port forwards (plan §4.4, §4.6). The Windows implementation (B7/B8)
/// materializes <see cref="ForwardTarget.Host"/> forwards as <c>netsh interface portproxy</c> rules
/// and reconciles them against the store at startup; <see cref="ForwardTarget.Client"/> forwards are
/// only recorded here and relayed to the owner's extension.
///
/// Forward state lives in the service, so it survives the user's PC being off (PC-independence).
/// </summary>
public interface IPortForwardManager
{
    /// <summary>
    /// Allocates (or returns the already allocated) public SSH port for a VM, from the configured SSH
    /// range, and records it durably on the VM before the host rule exists — so a crash can never leave
    /// a live rule that no stored allocation accounts for. Throws
    /// <see cref="PortRangeExhaustedException"/> when the range is full.
    /// </summary>
    Task<int> AllocateSshForwardAsync(string vmName, CancellationToken cancellationToken);

    /// <summary>Releases the VM's SSH forward. Returns false when it had none.</summary>
    Task<bool> ReleaseSshForwardAsync(string vmName, CancellationToken cancellationToken);

    /// <summary>
    /// Records a forward; <see cref="ForwardTarget.Host"/> targets additionally get a public port
    /// allocated from the app range and materialized on the host.
    ///
    /// The per-VM cap is enforced here rather than by the caller, so that counting and adding are one
    /// atomic step: a guest holding a VM token can otherwise fire concurrent requests past the cap.
    /// The same step re-checks that the VM still exists and is not being deleted, serialized against
    /// <see cref="RemoveAllForwardsAsync"/> — otherwise a forward could be added behind the back of the
    /// job that is tearing the VM down and survive it.
    /// </summary>
    Task<AddForwardResult> TryAddForwardAsync(
        string vmName,
        int vmPort,
        ForwardTarget target,
        string label,
        int maxForwards,
        CancellationToken cancellationToken);

    /// <summary>Removes one forward of a VM. Returns false when the id does not belong to that VM.</summary>
    Task<bool> RemoveForwardAsync(string vmName, string id, CancellationToken cancellationToken);

    /// <summary>Removes every forward of a VM (VM deletion). Returns how many were removed.</summary>
    Task<int> RemoveAllForwardsAsync(string vmName, CancellationToken cancellationToken);

    /// <summary>Lists forwards, for one VM or (when <paramref name="vmName"/> is null) all of them.</summary>
    Task<IReadOnlyList<PortForward>> ListAsync(string? vmName, CancellationToken cancellationToken);

    /// <summary>
    /// Re-materializes the stored forwards against the host's actual rules (netsh rules survive
    /// reboots; reconciliation heals drift and VM IP changes). Returns how many rules were repaired.
    /// </summary>
    Task<int> ReconcileAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Live proxied TCP connections for a VM across all of its forwards — the "no client
    /// connections" half of the idle check (plan §4.7).
    /// </summary>
    Task<int> CountActiveConnectionsAsync(string vmName, CancellationToken cancellationToken);
}

/// <summary>Why a forward was or was not added.</summary>
public enum AddForwardStatus
{
    Added,

    /// <summary>The VM already has the maximum number of forwards.</summary>
    LimitReached,

    /// <summary>The VM is gone or is being deleted; nothing new may be attached to it.</summary>
    VmUnavailable,
}

/// <param name="Forward">The new forward, when <paramref name="Status"/> is
/// <see cref="AddForwardStatus.Added"/>.</param>
public sealed record AddForwardResult(AddForwardStatus Status, PortForward? Forward)
{
    public static AddForwardResult Added(PortForward forward) => new(AddForwardStatus.Added, forward);

    public static AddForwardResult LimitReached { get; } = new(AddForwardStatus.LimitReached, null);

    public static AddForwardResult VmUnavailable { get; } = new(AddForwardStatus.VmUnavailable, null);
}

/// <summary>Thrown when a port range has no free port left.</summary>
public sealed class PortRangeExhaustedException(int start, int end)
    : InvalidOperationException($"No free port left in range {start}-{end}."), IConstructdError
{
    public int RangeStart { get; } = start;

    public int RangeEnd { get; } = end;
}
