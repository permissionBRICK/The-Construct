namespace Constructd.Core.Domain;

public sealed record GuestAddress(string Address, GuestAddressFamily Family, GuestAddressSource Source, DateTimeOffset ObservedAt, bool Verified, string? AdapterId = null);

/// <summary>Host-authoritative facts about ONE VM adapter (never guest-supplied).</summary>
public sealed record GuestAdapter(string VmId, string AdapterId, string MacAddress, bool MacSpoofingEnabled, string? SwitchName);
/// <summary>One host neighbor-table row on a guest-facing interface.</summary>
public sealed record HostNeighbor(string Address, string MacAddress, string InterfaceAlias, string State);
/// <summary>A guest subnet bound to the switch and host interface it belongs to, so overlapping subnets on different switches never match.</summary>
public sealed record GuestSubnet(string Cidr, string SwitchName, string InterfaceAlias);

public sealed record HostObservation(
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastBootAt,
    IReadOnlyList<GuestAddress> Addresses,
    string? StorageProblem);
