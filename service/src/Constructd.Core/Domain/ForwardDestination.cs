namespace Constructd.Core.Domain;

/// <summary>
/// Where a forward really connects. Null on every forward that exists today (a VM's own port),
/// so the flat legacy wire shape is unchanged.
/// </summary>
/// <param name="Via">The primary whose SSH endpoint carries a client tunnel; null for host forwards of a primary.</param>
/// <param name="Verified">Always false on Hyper-V in this delivery (§12.5); true only when an IAddressAuthority vouched for the address.</param>
public sealed record ForwardDestination(
    string VmName,
    string? Via,
    string? ConnectAddress,
    int ConnectPort,
    string RequestedBy,
    ForwardRelationship Relationship,
    bool Verified);
