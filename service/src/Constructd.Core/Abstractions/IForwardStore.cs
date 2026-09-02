using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

/// <summary>
/// Durable state of the extra port forwards (plan §4.4: forward state lives in the service, which is
/// what makes forwards survive the user's PC being off — and the service host rebooting).
///
/// The per-VM SSH forward is not in here: it is part of the VM record (<see cref="Vm.SshForwardPort"/>),
/// which is the canonical registry entry. <see cref="IPortForwardManager.ReconcileAsync"/> restores
/// both from their stores at startup: it reserves the ports in the allocators (so a new VM cannot be
/// handed a port that is already in use) and re-materializes the host's rules.
/// </summary>
public interface IForwardStore
{
    Task<PortForward?> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>Forwards of one VM, or (when <paramref name="vmName"/> is null) all of them.</summary>
    Task<IReadOnlyList<PortForward>> ListAsync(string? vmName, CancellationToken cancellationToken);

    Task<int> CountByVmAsync(string vmName, CancellationToken cancellationToken);

    Task AddAsync(PortForward forward, CancellationToken cancellationToken);

    /// <summary>
    /// Records what the owner's extension reported about a client forward (plan §4.6). Durable like
    /// the forward itself, so a service restart does not lose the link a guest is already printing.
    /// Returns false when the id is unknown; an existing ack is REPLACED, because the extension
    /// re-acks after re-establishing a tunnel (<c>docs/expose.md</c>).
    /// </summary>
    Task<bool> SetAckAsync(string id, ForwardAck ack, CancellationToken cancellationToken);

    Task<bool> RemoveAsync(string id, CancellationToken cancellationToken);
}
