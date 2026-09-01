namespace Constructd.Core.Domain;

/// <summary>
/// A port forward requested for a VM. <see cref="PublicPort"/> is set only for
/// <see cref="ForwardTarget.Host"/> forwards, which the service materializes on the service host;
/// <see cref="ForwardTarget.Client"/> forwards are recorded here and relayed to the owner's
/// extension, which opens them on the user's PC (plan §4.6).
/// </summary>
public sealed record PortForward(
    string Id,
    string VmName,
    int VmPort,
    int? PublicPort,
    ForwardTarget Target,
    string Label,
    DateTimeOffset Created);
