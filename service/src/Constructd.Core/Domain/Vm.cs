namespace Constructd.Core.Domain;

/// <summary>
/// A VM managed by this service. The service — not the user's PC — is the source of truth for
/// this record (plan §3.3 "canonical state is host-side").
/// </summary>
/// <param name="Name">Instance name, validated by <see cref="Logic.VmNameValidator"/>.</param>
/// <param name="Owner">Name of the owning <see cref="User"/>.</param>
/// <param name="SshForwardPort">Public SSH port allocated on the service host, once created.</param>
/// <param name="VmTokenHash">SHA-256 of the VM-scoped token injected at provision time.</param>
/// <param name="Forwards">
/// Extra port forwards. The authority for forwards is <see cref="Abstractions.IPortForwardManager"/>;
/// this list is a projection filled in when a VM is read out for the API.
/// </param>
/// <param name="Deleting">
/// Set when a removal job has been accepted for this VM. It fences the VM: no new forwards, no power
/// changes, no policy edits and no heartbeats, so nothing can be created behind the job that is
/// tearing the VM down. Its scoped token is revoked at the same moment.
/// </param>
public sealed record Vm(
    string Name,
    string Owner,
    int Cpu,
    int RamGb,
    int DiskGb,
    DateTimeOffset Created,
    VmState State,
    int? SshForwardPort,
    string? VmTokenHash,
    IdlePolicy IdlePolicy,
    IReadOnlyList<PortForward> Forwards,
    bool Deleting = false)
{
    public static IReadOnlyList<PortForward> NoForwards { get; } = Array.Empty<PortForward>();
}
