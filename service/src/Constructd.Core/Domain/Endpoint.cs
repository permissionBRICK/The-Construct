namespace Constructd.Core.Domain;

/// <summary>
/// Where a client dials a VM. Everything downstream (provisioner, extension SSH, probes) uses this
/// instead of a name convention (plan §4.2). Local mode: <c>&lt;name&gt;.mshome.net:22</c>; remote
/// mode: the service host plus the allocated forward port.
/// </summary>
/// <param name="PublicHost">
/// The name this VM's WEB forwards are advertised under (plan §4.12): the rendered
/// <c>Constructd:PublicHostPattern</c>, or the service's <c>PublicHost</c> when no pattern is
/// configured — in which case it equals <paramref name="SshHost"/>, which is why an EMPTY value
/// means exactly that and is what a driver-reported (host-internal) endpoint carries.
/// SSH is unaffected: a client still dials <paramref name="SshHost"/>:<paramref name="SshPort"/>.
/// </param>
public sealed record Endpoint(string SshHost, int SshPort, string PublicHost = "");
