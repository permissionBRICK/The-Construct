using System.Net;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Fakes;

public sealed class FakeGuestAddressProvider : IGuestAddressProvider
{
    public Dictionary<string, IReadOnlyList<GuestAddress>> Reported { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, IReadOnlyList<GuestAdapter>> Adapters { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<HostNeighbor> Neighbors { get; set; } = [];
    public IReadOnlyList<GuestSubnet> Subnets { get; set; } = [];
    public IReadOnlyList<IPAddress> HostAddresses { get; set; } = [];
    public Task<IReadOnlyList<GuestAddress>> GetReportedAddressesAsync(string vmName, CancellationToken ct) => Task.FromResult(Reported.GetValueOrDefault(vmName) ?? []);
    public Task<IReadOnlyList<GuestAdapter>> GetAdaptersAsync(string vmName, CancellationToken ct) => Task.FromResult(Adapters.GetValueOrDefault(vmName) ?? []);
    public Task<IReadOnlyList<HostNeighbor>> GetNeighborsAsync(CancellationToken ct) => Task.FromResult(Neighbors);
    public Task<IReadOnlyList<GuestSubnet>> GetGuestSubnetsAsync(CancellationToken ct) => Task.FromResult(Subnets);
    public Task<IReadOnlyList<IPAddress>> GetHostAddressesAsync(CancellationToken ct) => Task.FromResult(HostAddresses);
}
