using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
namespace Constructd.Core.Services;

public sealed class HostNetworkPolicy(IHostConfigStore config) : IHostNetworkPolicy
{
    public async Task<NetworkConfig> GetAsync(CancellationToken ct) => await config.GetAsync<NetworkConfig>("network", ct) ?? HostAdminDefaults.Network;
}
