using Constructd.Core.Abstractions;
namespace Constructd.Fakes;

public sealed class InMemoryNetworkRuleStore : INetworkRuleStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IReadOnlyList<NetworkRule>> _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _history = new(StringComparer.OrdinalIgnoreCase);
    public Task ReplaceAsync(string vmName, IReadOnlyList<NetworkRule> rules, CancellationToken ct)
    { lock (_gate) _rules[vmName] = rules; return Task.CompletedTask; }
    public Task<IReadOnlyList<NetworkRule>> ListAsync(string? vmName, CancellationToken ct)
    { lock (_gate) return Task.FromResult<IReadOnlyList<NetworkRule>>(vmName is null ? _rules.Values.SelectMany(x => x).ToArray() : _rules.GetValueOrDefault(vmName) ?? []); }
    public Task RememberAddressAsync(string vmName, string address, CancellationToken ct)
    { lock (_gate) { if (!_history.TryGetValue(vmName, out var values)) _history[vmName] = values = []; values.Add(address); } return Task.CompletedTask; }
    public Task<IReadOnlyList<string>> PreviousAddressesAsync(string vmName, CancellationToken ct)
    { lock (_gate) return Task.FromResult<IReadOnlyList<string>>(_history.TryGetValue(vmName, out var values) ? values.ToArray() : []); }
}
