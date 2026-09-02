namespace Constructd.Core.Domain;

/// <summary>
/// A port forward requested for a VM. <see cref="PublicPort"/> is set only for
/// <see cref="ForwardTarget.Host"/> forwards, which the service materializes on the service host;
/// <see cref="ForwardTarget.Client"/> forwards are recorded here and relayed to the owner's
/// extension, which opens them on the user's PC (plan §4.6) and reports back through
/// <see cref="Ack"/>.
/// </summary>
/// <param name="Ack">
/// What the owner's extension reported about a <see cref="ForwardTarget.Client"/> forward, or
/// <c>null</c> while nobody has picked it up. It is written by the OWNER (never by the VM's own
/// token — a guest must not be able to fake the answer to its own question) through
/// <c>POST /vms/{name}/forwards/{id}/ack</c>, and it is what turns the forward's advisory
/// <c>url</c> from null into a link.
/// </param>
public sealed record PortForward(
    string Id,
    string VmName,
    int VmPort,
    int? PublicPort,
    ForwardTarget Target,
    string Label,
    DateTimeOffset Created,
    ForwardAck? Ack = null);

/// <summary>
/// The client half of a forward: the extension opened <see cref="LocalPort"/> on the user's PC, or
/// could not and says why. Mirrors the spool ack document of <c>docs/expose.md</c> field for field,
/// because <c>construct expose</c> reads the two with the same lenient parser.
/// </summary>
/// <param name="HostLabel">
/// Optional name of the user's PC. Present ⇒ the link is <c>http://&lt;hostLabel&gt;:&lt;localPort&gt;/</c>;
/// absent ⇒ loopback, which is the default and the only thing an untouched install produces.
/// </param>
/// <param name="Message">Why it failed. Empty for <see cref="AckStatus.Open"/>.</param>
public sealed record ForwardAck(
    AckStatus Status,
    int? LocalPort,
    string? HostLabel,
    string Message,
    DateTimeOffset At);

/// <summary>Whether the client opened the port or failed to.</summary>
public enum AckStatus
{
    Open,
    Error,
}
