using Constructd.Core.Domain;
namespace Constructd.Core.Abstractions;

public interface INetworkRuleStore
{
    Task ReplaceAsync(string vmName, IReadOnlyList<NetworkRule> rules, CancellationToken ct);
    Task<IReadOnlyList<NetworkRule>> ListAsync(string? vmName, CancellationToken ct);
    Task RememberAddressAsync(string vmName, string address, CancellationToken ct);
    Task<IReadOnlyList<string>> PreviousAddressesAsync(string vmName, CancellationToken ct);
}
