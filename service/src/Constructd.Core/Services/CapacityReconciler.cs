using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Core.Services;

/// <summary>No ledger gate while acquiring a VM gate or calling the hypervisor. A busy VM is skipped.</summary>
public sealed class CapacityReconciler(IHypervisorInventory inventory, IVmRepository vms, IHypervisorDriver driver,
    IVmOperationGate gates, ICapacityReconciliationStore store, IChildLeaseReconciler? leases = null)
{
    private readonly SemaphoreSlim _pass = new(1, 1);
    public async Task<IReadOnlyList<OrphanOutcome>> ReconcileAsync(CancellationToken ct)
    {
        await _pass.WaitAsync(ct);
        try
        {
            var managed = await vms.ListAsync(null, ct);
            var captured = await store.ReadReservationsAsync(ct);
            // This pass is evidence only. Admission takes its own fresh epoch after a mutation.
            var snapshot = await inventory.ReadAsync(captured, ct);
            var outcomes = new List<OrphanOutcome>();
            foreach (var vm in managed)
            {
                await using var handle = await gates.TryAcquireAsync(vm.Name, "capacity-reconcile", ct);
                if (handle is null) continue;
                VmState state;
                try { state = await driver.GetStateAsync(vm.Name, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { state = VmState.Unknown; }
                outcomes.AddRange(await store.ApplyVmAsync(vm, state, snapshot, captured, ct));
                if (leases is not null) await leases.ReconcileAsync(vm, state, ct);
            }
            outcomes.AddRange(await store.ApplyHostAsync(snapshot, captured, ct));
            return outcomes;
        }
        finally { _pass.Release(); }
    }
}

/// <summary>Uses persisted liabilities for allowance usage, including media and pending operations.</summary>
public sealed class CapacityDelegationPolicy(IDelegationPolicy policy, ICapacityLedger ledger, IVmRepository vms) : IDelegationPolicy
{
    public Task<EffectiveAllowance> ResolveAsync(string owner, string? parentVm, CancellationToken ct) => policy.ResolveAsync(owner, parentVm, ct);
    public Task<IReadOnlyList<ChildAction>> AllowedActionsAsync(Vm vm, string principal, ForwardRelationship relationship, CancellationToken ct) => policy.AllowedActionsAsync(vm, principal, relationship, ct);
    public async Task<AllowanceUsage> UsageAsync(string owner, CancellationToken ct)
    {
        var rows = (await ledger.SnapshotAsync(false, ct)).Reservations.Where(r => string.Equals(r.ScopeOwner, owner, StringComparison.OrdinalIgnoreCase)).ToArray();
        var owned = await vms.ListAsync(owner, ct);
        return new(owned.Count(v => v.Kind == VmKind.Primary), owned.Count(v => v.Kind == VmKind.Child),
            checked((int)rows.Where(r => r.Resource == ReservationResource.Cpu).Sum(r => r.Amount)),
            rows.Where(r => r.Resource == ReservationResource.Ram).Sum(r => r.Amount), rows.Where(r => r.Resource == ReservationResource.Storage).Sum(r => r.Amount));
    }
}
