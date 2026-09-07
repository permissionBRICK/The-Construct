using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Jobs;

public sealed class PrimaryVmJobs(IServiceScopeFactory scopes, IVmOperationGate gates, IVmRepository vms,
    IHypervisorDriver driver, IChildVmDriver childDriver, IVmMetadataStore metadata,
    ICapacityLedger capacity, IHypervisorInventory inventory, IAdmissionStore admission)
{
    public async Task<JobOutcome> CreateAsync(Job job, Vm admitted, VmDescriptor descriptor,
        IReadOnlyList<string> reservationIds, bool redownload, IProgress<string> progress, CancellationToken ct)
    {
        await using var gate = await gates.AcquireAsync(admitted.Name, job.Id, ct);
        try
        {
            await capacity.ExtendAsync(reservationIds, TimeSpan.FromHours(2), ct);
            var result = await VmJobs.CreateAsync(scopes, descriptor, admitted.Owner, progress, ct, redownload);
            var vm = await vms.GetAsync(admitted.Name, ct) ?? throw new LifecycleException("vm-deleting");
            var state = await driver.GetStateAsync(vm.Name, ct);
            var incarnation = await childDriver.GetVmIdAsync(vm.Name, ct);
            if (incarnation is not null) await metadata.UpdateIncarnationAsync(vm.Name, incarnation, ct);
            var confirmed = await admission.MutateAsync(null, async scope =>
            {
                if (!await scope.UpdatePowerStateAsync(vm.Name, state, vm.PowerGeneration)) return false;
                await scope.ConfirmReservationsAsync(reservationIds, state); return true;
            }, ct);
            if (confirmed.Outcome != AdmissionOutcome.Accepted) throw new LifecycleException("power-state-changed");
            return result;
        }
        catch
        {
            // The unchanged provisioner performs its existing rollback. Accounting needs its own
            // per-artifact evidence; disappearance of the registry or hypervisor alone proves no disk deletion.
            try
            {
                var rows = (await capacity.SnapshotAsync(false, CancellationToken.None)).Reservations.Where(r => r.OperationId == job.Id).ToArray();
                var observed = await driver.GetStateAsync(admitted.Name, CancellationToken.None);
                await capacity.ReleaseAsync(rows.Where(r => r.Resource != ReservationResource.Storage || ReservationRules.SavedState(r)).Select(r => r.Id).ToArray(), observed, "primary-create-failure", CancellationToken.None);
                var evidence = await inventory.ReadAsync(rows, CancellationToken.None);
                var absent = rows.Where(r => r.Resource == ReservationResource.Storage && !ReservationRules.SavedState(r) &&
                    evidence.Artifacts?.Any(a => r.Artifact == a.Artifact && a.Presence == ArtifactPresence.Absent) == true).Select(r => r.Id).ToArray();
                await capacity.ReleaseAsync(absent, VmState.Absent, "primary-artifact-absence", CancellationToken.None);
            }
            catch { /* Failed observation retains capacity. */ }
            throw;
        }
    }
}
