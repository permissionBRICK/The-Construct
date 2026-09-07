using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

/// <summary>
/// RESERVED: an IP allocation authority (a host-controlled DHCP/IPAM that assigns addresses to adapters).
/// No implementation exists in this delivery; without one, IP ownership cannot be verified (§12.5).
/// </summary>
public interface IAddressAuthority
{
    Task<IReadOnlyList<GuestAddress>> GetAssignedAddressesAsync(string vmName, CancellationToken ct);
}

public interface IGuestAddressProvider
{
    /// <summary>Guest-reported (KVP) addresses for THIS VM's adapters (by VM id). Untrusted input; empty is normal (no OS yet).</summary>
    Task<IReadOnlyList<GuestAddress>> GetReportedAddressesAsync(string vmName, CancellationToken ct);
    /// <summary>Host-authoritative adapter facts for this VM (by VM id): MAC and the MAC-spoofing setting.</summary>
    Task<IReadOnlyList<GuestAdapter>> GetAdaptersAsync(string vmName, CancellationToken ct);
    /// <summary>Host neighbor table rows on guest-facing (vEthernet) interfaces: address → MAC as the host observed it.</summary>
    Task<IReadOnlyList<HostNeighbor>> GetNeighborsAsync(CancellationToken ct);
    /// <summary>The host-side guest subnets, each bound to its switch and interface; empty ⇒ no address usable.</summary>
    Task<IReadOnlyList<GuestSubnet>> GetGuestSubnetsAsync(CancellationToken ct);
    Task<IReadOnlyList<System.Net.IPAddress>> GetHostAddressesAsync(CancellationToken ct);
}

public sealed record ForwardRequest(
    string RequesterPrincipal,
    ForwardRelationship Relationship,
    string TargetVm,
    string? Via,
    ForwardTarget Target,
    int VmPort,
    int ConnectPort,
    string Label,
    int MaxForwards);
public enum ExposeStatus { Added, LimitReached, VmUnavailable, AddressUnverifiable, PolicyDenied }
public sealed record ExposeResult(ExposeStatus Status, PortForward? Forward, string? Detail);
public interface IAccessExposure
{
    /// <summary>Validates the destination address (§12.5) and materializes under the target VM's forward gate.</summary>
    Task<ExposeResult> TryExposeAsync(ForwardRequest request, CancellationToken ct);
    Task<int> RevokeForRequesterAsync(string targetVm, string requesterPrincipal, CancellationToken ct);
    Task<int> RevokeNonOwnerAsync(string targetVm, CancellationToken ct);
    Task<IReadOnlyList<PortForward>> ListViaAsync(string viaVm, CancellationToken ct);
}

public sealed record NetworkRule(string Id, string VmName, string Peer, string Kind, string State, DateTimeOffset Created, DateTimeOffset Updated);
public interface INetworkPolicyReconciler
{
    /// <summary>"none" for every backend in this delivery; never "enforced" without an enforcing adapter.</summary>
    string IsolationLevel { get; }
    Task OnVmCreatedAsync(Vm vm, CancellationToken ct);
    Task OnVmDeletedAsync(string vmName, CancellationToken ct);
    Task OnSharingChangedAsync(Vm vm, SharingScope previous, CancellationToken ct);
    Task OnAddressChangedAsync(string vmName, IReadOnlyList<GuestAddress> addresses, CancellationToken ct);
    Task<int> ReconcileAsync(CancellationToken ct);
    Task<IReadOnlyList<NetworkRule>> ListRulesAsync(string vmName, CancellationToken ct);
}

public interface IHostNetworkPolicy
{
    Task<NetworkConfig> GetAsync(CancellationToken ct);
}
