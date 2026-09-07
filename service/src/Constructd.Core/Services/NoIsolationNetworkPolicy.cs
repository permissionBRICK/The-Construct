using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Core.Services;

/// <summary>Bookkeeping only. No backend in this delivery enforces packet isolation.</summary>
public sealed class NoIsolationNetworkPolicy(IClock clock) : INetworkPolicyReconciler
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, NetworkRule> _rules = new(StringComparer.OrdinalIgnoreCase);
    public string IsolationLevel => "none";
    public Task OnVmCreatedAsync(Vm vm, CancellationToken ct) => RecordAsync(vm);
    public Task OnSharingChangedAsync(Vm vm, SharingScope previous, CancellationToken ct) => RecordAsync(vm);
    private Task RecordAsync(Vm vm)
    {
        lock (_gate) _rules[vm.Name] = new(vm.Name, vm.Name, vm.Sharing == SharingScope.Host ? "host" : vm.Owner,
            "intent", "unenforced", _rules.GetValueOrDefault(vm.Name)?.Created ?? clock.UtcNow, clock.UtcNow);
        return Task.CompletedTask;
    }
    public Task OnVmDeletedAsync(string vmName, CancellationToken ct) { lock (_gate) _rules.Remove(vmName); return Task.CompletedTask; }
    public Task OnAddressChangedAsync(string vmName, IReadOnlyList<GuestAddress> addresses, CancellationToken ct)
    { lock (_gate) if (_rules.TryGetValue(vmName, out var rule)) _rules[vmName] = rule with { Updated = clock.UtcNow }; return Task.CompletedTask; }
    public Task<int> ReconcileAsync(CancellationToken ct) => Task.FromResult(0); // No enforcing adapter exists.
    public Task<IReadOnlyList<NetworkRule>> ListRulesAsync(string vmName, CancellationToken ct)
    { lock (_gate) return Task.FromResult<IReadOnlyList<NetworkRule>>(_rules.TryGetValue(vmName, out var rule) ? [rule] : []); }
}
