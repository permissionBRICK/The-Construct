using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Jobs;

public sealed class ChildDeleteJob(IVmRepository vms, IVmDelegationRepository metadata, IChildVmDriver driver,
    IHypervisorDriver hypervisor, IChildVmCreationOwnership ownership, ICapacityLedger capacity, IMediaStore media, IMediaGate mediaGate,
    IMediaTransfer transfer, IPortForwardManager forwards, INetworkPolicyReconciler network,
    IVmOperationGate vmGate, IPersistedJobRunner runner, IAuditLog audit, IClock clock)
{
    public async Task<JobOutcome> RunAsync(Job job, Vm vm, IProgress<string> progress, CancellationToken ct)
    {
        await using var gate = await vmGate.AcquireAsync(vm.Name, job.Id, ct);
        try
        {
            await Phase("fence");
            var current = await vms.GetAsync(vm.Name, ct);
            if (current is null) return new(new { name = vm.Name, outcome = "removed", retained = Array.Empty<object>() });
            if (current.Kind != VmKind.Child || current.Incarnation != vm.Incarnation) throw new ChildValidationException("vm-incarnation-conflict", "vm");
            if (!current.Deleting && !await metadata.TryFenceAsync(vm.Name, job.Id, false, ct)) throw new ChildValidationException("vm-deleting", "vm");
            await CleanupAsync(vm, progress, Phase, ct);
            await Phase("done");
            await Audit("succeeded");
            return new(new { name = vm.Name, outcome = "removed", retained = Array.Empty<object>() });
        }
        catch (Exception ex)
        {
            await Audit("failed");
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            throw new JobFailureException(SafeError.Describe(ex), new
            {
                name = vm.Name, outcome = "failed",
                retained = new[] { new { artifact = vm.Name, reason = "Cleanup has not confirmed removal of all child artifacts; ownership and remaining capacity are retained." } }
            });
        }
        async Task Phase(string phase) { await runner.SetPhaseAsync(job.Id, phase, ct); progress.Report("Child deletion: " + phase + "."); }
        Task Audit(string outcome) => audit.AppendAsync(new(clock.UtcNow, job.Initiator ?? job.Owner, "child.delete", vm.Name, outcome == "succeeded" ? AuditOutcome.Success : AuditOutcome.Failure, $"op=child.delete, owner={vm.Owner}, parent={vm.Parent}, target={vm.Name}, job={job.Id}"), CancellationToken.None);
    }

    // Called while holding the VM gate. A successful driver removal certifies owned disk AND saved-state removal.
    public async Task CleanupAsync(Vm vm, IProgress<string> progress, Func<string, Task>? phase, CancellationToken ct, bool deleteDedicated = true)
    {
        if (phase is not null) await phase("power");
        var id = await driver.GetVmIdAsync(vm.Name, ct);
        if (id is not null && vm.Incarnation is not null && id != vm.Incarnation) throw new ChildValidationException("vm-incarnation-conflict", "vm");
        if (id is not null && vm.Incarnation is null)
        {
            var operation = await ownership.GetCreationOperationAsync(vm.Name, ct);
            var holds = (await capacity.SnapshotAsync(false, ct)).Reservations;
            if (operation is null || !holds.Any(x => x.VmName == vm.Name && x.OperationId == operation))
                throw new ChildValidationException("artifact-ownership-unverified", "vm");
        }
        if (phase is not null) await phase("vm");
        await driver.RemoveAsync(vm.Name, progress, ct);
        if (await hypervisor.GetStateAsync(vm.Name, ct) != VmState.Absent) throw new ChildValidationException("cleanup-unverified", "vm");
        if (phase is not null) await phase("forwards");
        await forwards.RemoveAllForwardsAsync(vm.Name, ct);
        await network.OnVmDeletedAsync(vm.Name, ct);
        if (phase is not null) await phase("media-references");
        foreach (var reference in await media.ListReferencesForVmAsync(vm.Name, ct))
        {
            await using var handle = await mediaGate.AcquireAsync(reference.MediaId, "delete:" + vm.Name, ct);
            await media.RemoveReferenceAsync(reference.MediaId, vm.Name, reference.Slot, ct);
        }
        if (phase is not null) await phase("dedicated-media");
        if (deleteDedicated)
        {
            // DedicatedTo remains discoverable after references are removed (including retries).
            foreach (var item in (await media.ListAsync(vm.Owner, ct)).Where(x => Ownership.SameName(x.DedicatedTo, vm.Name)))
            {
                await using var handle = await mediaGate.AcquireAsync(item.Id, "delete:" + vm.Name, ct);
                var current = await media.GetAsync(item.Id, ct);
                if (current is null) continue;
                if ((await media.ListReferencesAsync(item.Id, ct)).Count > 0) throw new ChildValidationException("cleanup-retained", "dedicated-media");
                if (current.State != MediaState.Deleting && !await media.TryTransitionAsync(item.Id, current.State, current with { State = MediaState.Deleting }, ct)) throw new ChildValidationException("cleanup-retained", "dedicated-media");
                if (!await transfer.TryDeleteAsync(item.Path, ct)) throw new ChildValidationException("cleanup-retained", "dedicated-media");
                var mediaIds = (await capacity.SnapshotAsync(false, ct)).Reservations.Where(x => x.Artifact == "media:" + item.Id).Select(x => x.Id).ToArray();
                await capacity.ReleaseAsync(mediaIds, VmState.Absent, "dedicated media removed", ct);
                await media.RemoveAsync(item.Id, ct);
            }
        }
        if (phase is not null) await phase("storage");
        var ids = (await capacity.SnapshotAsync(false, ct)).Reservations.Where(x => x.VmName == vm.Name).Select(x => x.Id).ToArray();
        await capacity.ReleaseAsync(ids, VmState.Absent, "child artifacts confirmed removed", ct);
        await vms.RemoveAsync(vm.Name, ct);
    }
}
