namespace Constructd.Core.Domain;

/// <summary>
/// Where a client dials a VM. Everything downstream (provisioner, extension SSH, probes) uses this
/// instead of a name convention (plan §4.2). Local mode: <c>&lt;name&gt;.mshome.net:22</c>; remote
/// mode: the service host plus the allocated forward port.
/// </summary>
public sealed record Endpoint(string SshHost, int SshPort);
