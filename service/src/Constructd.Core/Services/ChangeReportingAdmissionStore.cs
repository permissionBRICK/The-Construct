using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Core.Services;

/// <summary>
/// The admission store with its VM changes on the <see cref="ChangeFeed"/>: a VM it inserts
/// (<c>vm.created</c>), fences or gives a job (<c>vm.changed</c>), and the VMs a committed mutation
/// touched (<c>vm.power</c> for a power state, <c>vm.changed</c> for the rest). Announced only after
/// the store accepted, so a refused or conflicting mutation says nothing.
/// </summary>
public sealed class ChangeReportingAdmissionStore(IAdmissionStore inner, IVmRepository vms, ChangeFeed changes) : IAdmissionStore
{
    public async Task<AdmissionResult> AdmitAsync(AdmissionPlan plan, CancellationToken ct)
    {
        var result = await inner.AdmitAsync(plan, ct).ConfigureAwait(false);
        if (result.Outcome != AdmissionOutcome.Accepted) return result;
        if (plan.VmToInsert is { } inserted) changes.Vm("vm.created", inserted);
        var touched = new[] { plan.VmToFence, plan.VmToAssignJob, plan.VmSourceCommit?.VmName }
            .Concat(result.Cascade?.CurrentChildren.Select(c => c.Name) ?? [])
            .Where(n => n is not null && !Logic.Ownership.SameName(n, plan.VmToInsert?.Name))
            .Distinct(Logic.Ownership.NameComparer);
        foreach (var name in touched) await AnnounceAsync(name!, "vm.changed", ct).ConfigureAwait(false);
        return result;
    }

    public async Task<AdmissionResult> MutateAsync(OperationKeyRecord? key, Func<IAdmissionScope, Task<bool>> mutation, CancellationToken ct)
    {
        var touched = new Dictionary<string, bool>(Logic.Ownership.NameComparer);
        var result = await inner.MutateAsync(key, scope => mutation(new Recording(scope, touched)), ct).ConfigureAwait(false);
        if (result.Outcome != AdmissionOutcome.Accepted) return result;
        foreach (var (name, power) in touched.ToArray()) await AnnounceAsync(name, power ? "vm.power" : "vm.changed", ct).ConfigureAwait(false);
        return result;
    }

    public Task MarkStartFailedAsync(string jobId, string error, CancellationToken ct) => inner.MarkStartFailedAsync(jobId, error, ct);

    private async Task AnnounceAsync(string name, string type, CancellationToken ct)
    {
        if (await vms.GetAsync(name, CancellationToken.None).ConfigureAwait(false) is { } vm) changes.Vm(type, vm);
    }

    /// <summary>Passes every call through and notes which VMs a write touched, and whether its power state.</summary>
    private sealed class Recording(IAdmissionScope inner, Dictionary<string, bool> touched) : IAdmissionScope
    {
        private bool Note(string vm, bool written, bool power = false)
        {
            if (written) lock (touched) touched[vm] = power || touched.GetValueOrDefault(vm);
            return written;
        }

        public async Task<bool> UpdateLeaseAsync(string vmName, Lease lease, long expectedVersion) =>
            Note(vmName, await inner.UpdateLeaseAsync(vmName, lease, expectedVersion).ConfigureAwait(false));

        public async Task<bool> UpdateSharingAsync(string vmName, SharingScope scope) =>
            Note(vmName, await inner.UpdateSharingAsync(vmName, scope).ConfigureAwait(false));

        public Task SetOverrideAsync(VmOverride value) => inner.SetOverrideAsync(value);

        public Task<bool> SetAllowanceAsync(string userName, UserAllowance allowance) => inner.SetAllowanceAsync(userName, allowance);

        public async Task<bool> UpdateHardwareAsync(string vmName, ChildHardware hardware, long expectedGeneration) =>
            Note(vmName, await inner.UpdateHardwareAsync(vmName, hardware, expectedGeneration).ConfigureAwait(false));

        public async Task<bool> UpdatePrimaryRamAsync(string vmName, int ramGb, long expectedGeneration) =>
            Note(vmName, await inner.UpdatePrimaryRamAsync(vmName, ramGb, expectedGeneration).ConfigureAwait(false));

        public async Task<bool> UpdateIdlePolicyAsync(string vmName, IdlePolicy policy, long expectedGeneration) =>
            Note(vmName, await inner.UpdateIdlePolicyAsync(vmName, policy, expectedGeneration).ConfigureAwait(false));

        public async Task<bool> UpdatePrimaryCpuAsync(string vmName, int cpus, long expectedGeneration) =>
            Note(vmName, await inner.UpdatePrimaryCpuAsync(vmName, cpus, expectedGeneration).ConfigureAwait(false));

        public async Task<bool> UpdatePowerStateAsync(string vmName, VmState state, long expectedGeneration) =>
            Note(vmName, await inner.UpdatePowerStateAsync(vmName, state, expectedGeneration).ConfigureAwait(false), power: true);

        public Task<bool> BumpPowerGenerationAsync(string vmName, long expected) => inner.BumpPowerGenerationAsync(vmName, expected);

        public Task<Vm?> ReadVmAsync(string vmName) => inner.ReadVmAsync(vmName);

        public Task<CapacityDecision> ReserveAsync(ReservationRequest request) => inner.ReserveAsync(request);

        public Task ConfirmReservationsAsync(IReadOnlyList<string> ids, VmState observed) => inner.ConfirmReservationsAsync(ids, observed);

        public Task ReleaseReservationsAsync(IReadOnlyList<string> ids, VmState observed, string reason) => inner.ReleaseReservationsAsync(ids, observed, reason);

        public Task<bool> CompleteOperationKeyAsync(string owner, string kind, string key, string responseJson) => inner.CompleteOperationKeyAsync(owner, kind, key, responseJson);

        public Task AppendAuditAsync(AuditEntry entry) => inner.AppendAuditAsync(entry);
    }
}
