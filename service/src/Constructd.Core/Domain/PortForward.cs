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
/// <param name="Https">
/// The host terminates TLS on <see cref="PublicPort"/> with the host's own HTTPS certificate and relays the
/// decrypted stream to the VM port (docs/https-forwards.md). Only for <see cref="ForwardTarget.Host"/>
/// forwards of a primary VM; the certificate's key never enters the guest.
/// </param>
/// <param name="Main">
/// This HTTPS forward is the VM's main web UI: the host's shared HTTPS port also serves it under the VM's
/// own public name, without a port in the URL. The newest main forward of a VM wins.
/// </param>
/// <param name="UpstreamTls">
/// Whether the VM port itself speaks TLS (the host then connects with TLS and does not verify the guest's
/// certificate). Null: detected on the first connection and remembered for a while.
/// </param>
public sealed record PortForward(
    string Id,
    string VmName,
    int VmPort,
    int? PublicPort,
    ForwardTarget Target,
    string Label,
    DateTimeOffset Created,
    ForwardAck? Ack = null,
    ForwardDestination? Destination = null,
    bool Https = false,
    bool Main = false,
    bool? UpstreamTls = null);

/// <summary>What makes a host forward an HTTPS forward (docs/https-forwards.md).</summary>
public sealed record HttpsForwardOptions(bool Main = false, bool? UpstreamTls = null);

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
