using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Core.Services;

/// <summary>Durable intended rules only. No packet filtering or IP ownership authority is installed.</summary>
public sealed class NoIsolationNetworkPolicy(IClock clock, INetworkRuleStore store, IVmRepository vms,
    IUserStore users, IAccessExposure exposure, IAuditLog audit, IGuestAddressProvider addresses) : INetworkPolicyReconciler
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _observed = new(StringComparer.OrdinalIgnoreCase);
    public string IsolationLevel => "none";
    public Task OnVmCreatedAsync(Vm vm, CancellationToken ct) => RecordAsync(vm, ct);
    public async Task OnSharingChangedAsync(Vm vm, SharingScope previous, CancellationToken ct)
    {
        if (vm.Sharing != SharingScope.Host) await exposure.RevokeNonOwnerAsync(vm.Name, ct);
        await RecordAsync(vm, ct);
    }
    private async Task RecordAsync(Vm vm, CancellationToken ct)
    {
        var rules = new List<NetworkRule>();
        var old = await store.ListAsync(vm.Name, ct);
        void Add(string peer, string kind)
        {
            var id = (vm.Name + ":" + kind + ":" + peer).ToLowerInvariant();
            rules.Add(new(id, vm.Name, peer, kind, "intended", old.FirstOrDefault(r => r.Id == id)?.Created ?? clock.UtcNow, clock.UtcNow));
        }
        if (vm.Kind == VmKind.Child && vm.Parent is not null && !vm.Deleting)
        {
            Add(vm.Parent, "parent-child");
            if (vm.Sharing == SharingScope.Host && await users.GetAsync(vm.Owner, ct) is { Enabled: true })
                foreach (var primary in (await vms.ListAsync(null, ct)).Where(p => p.Kind == VmKind.Primary && !p.Deleting && !Ownership.SameName(p.Name, vm.Parent)))
                    if (await users.GetAsync(primary.Owner, ct) is { Enabled: true }) Add(primary.Name, "shared-consumer");
        }
        if (old.Count == rules.Count && old.All(r => rules.Any(n => n.Id == r.Id && n.State == r.State))) return;
        await store.ReplaceAsync(vm.Name, rules, ct);
        await audit.AppendAsync(new(clock.UtcNow, "system", "network.intent", vm.Name, AuditOutcome.Success,
            "isolation=none, rules=" + rules.Count), ct);
    }
    public async Task OnVmDeletedAsync(string vmName, CancellationToken ct)
    {
        _observed.TryRemove(vmName, out _);
        if ((await store.ListAsync(vmName, ct)).Count == 0) return;
        await store.ReplaceAsync(vmName, [], ct);
        await audit.AppendAsync(new(clock.UtcNow, "system", "network.revoke", vmName, AuditOutcome.Success, "isolation=none"), ct);
    }
    public async Task OnAddressChangedAsync(string vmName, IReadOnlyList<GuestAddress> addresses, CancellationToken ct)
    { if (await vms.GetAsync(vmName, ct) is { } vm) await RecordAsync(vm, ct); }
    public Task<int> ReconcileAsync(CancellationToken ct) => ReconcileCoreAsync(null, ct);
    public Task<int> ReconcileAsync(IGuestAddressProvider snapshot, CancellationToken ct) => ReconcileCoreAsync(snapshot, ct);
    private async Task<int> ReconcileCoreAsync(IGuestAddressProvider? snapshot, CancellationToken ct)
    {
        var current = await vms.ListAsync(null, ct);
        foreach (var name in (await store.ListAsync(null, ct)).Select(r => r.VmName).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!current.Any(v => Ownership.SameName(v.Name, name))) await OnVmDeletedAsync(name, ct);
        if (current.Any(v => v.Kind == VmKind.Child))
            snapshot ??= addresses is IGuestAddressSnapshotProvider snapshots ? await snapshots.CaptureAsync(ct) : addresses;
        foreach (var vm in current.Where(v => v.Kind == VmKind.Child))
        {
            // Also repairs a missed sharing event after a service interruption.
            await OnSharingChangedAsync(vm, vm.Sharing, ct);
            var reported = await snapshot!.GetReportedAddressesAsync(vm.Name, ct);
            var fingerprint = string.Join(";", reported.Select(a => a.Address + ":" + a.AdapterId).Order(StringComparer.Ordinal));
            if (!_observed.TryGetValue(vm.Name, out var previous) || previous != fingerprint)
            {
                await OnAddressChangedAsync(vm.Name, reported, ct);
                _observed[vm.Name] = fingerprint;
            }
        }
        return 0; // Intended state is not an enforced rule count.
    }
    public Task<IReadOnlyList<NetworkRule>> ListRulesAsync(string vmName, CancellationToken ct) => store.ListAsync(vmName, ct);
}
