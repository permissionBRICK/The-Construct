using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Jobs;

public sealed class CascadeJobs(IVmRepository vms, IVmDelegationRepository delegation, IHypervisorDriver driver,
    IChildVmDriver childDriver, ChildDeleteJob children, IVmOperationGate gates, IPortForwardManager forwards,
    ICapacityLedger capacity, IMediaStore media, INetworkPolicyReconciler network,
    IPersistedJobRunner runner, IAuditLog audit, IClock clock, IServiceScopeFactory scopes)
{
    public async Task<JobOutcome> RunAsync(Job job, Vm parent, CascadePreview preview, IProgress<string> progress, CancellationToken ct)
    {
        await using var gate = await gates.AcquireAsync(parent.Name, job.Id, ct);
        if (job.Kind == "remove-vm") return await RemovePrimaryAsync(job, parent, preview, progress, ct);
        var outcomes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var saved = preview with { State = CascadeState.Running, JobId = job.Id, Outcomes = outcomes };
        try
        {
            await Phase("fence");
            if (await vms.GetAsync(parent.Name, ct) is not { Deleting: true, ChildCreationClosed: true } current || current.Incarnation != parent.Incarnation)
                throw new LifecycleException("vm-incarnation-conflict");
            await Phase("children");
            foreach (var expected in preview.Children)
            {
                await using var childGate = await gates.AcquireAsync(expected.Name, job.Id, ct);
                var child = await vms.GetAsync(expected.Name, ct);
                try
                {
                    if (child is not null)
                    {
                        if (child.Incarnation != expected.Incarnation || !Ownership.SameName(child.Parent, parent.Name) || !child.Deleting)
                            throw new LifecycleException("vm-incarnation-conflict");
                        await children.CleanupAsync(child, progress, null, ct);
                    }
                    outcomes[expected.Name] = "removed";
                }
                catch (Exception ex)
                {
                    outcomes[expected.Name] = "retained:" + SafeError.Describe(ex);
                }
                await delegation.SaveCascadePreviewAsync(saved with { Outcomes = new Dictionary<string,string>(outcomes) }, CancellationToken.None);
                ct.ThrowIfCancellationRequested();
            }
            if (outcomes.Values.Any(value => value != "removed") || (await delegation.ListChildrenAsync(parent.Name, ct)).Count != 0)
                throw new LifecycleException("cascade-cleanup-incomplete");
            await Phase("primary");
            var id = await childDriver.GetVmIdAsync(parent.Name, ct);
            if (id is not null && parent.Incarnation is not null && id != parent.Incarnation) throw new LifecycleException("vm-incarnation-conflict");
            await driver.RemoveVmAsync(parent.Name, progress, ct);
            if (await driver.GetStateAsync(parent.Name, ct) != VmState.Absent) throw new LifecycleException("cleanup-unverified");
            await Phase("forwards");
            var removed = await forwards.RemoveAllForwardsAsync(parent.Name, ct);
            await forwards.ReleaseSshForwardAsync(parent.Name, ct);
            await Phase("media-references");
            foreach (var reference in await media.ListReferencesForVmAsync(parent.Name, ct))
                await media.RemoveReferenceAsync(reference.MediaId, parent.Name, reference.Slot, ct);
            await Phase("network");
            await network.OnVmDeletedAsync(parent.Name, ct);
            await delegation.RemoveOverrideAsync(parent.Name, ct);
            var reservations = (await capacity.SnapshotAsync(false, ct)).Reservations.Where(r => Ownership.SameName(r.VmName, parent.Name)).Select(r => r.Id).ToArray();
            await capacity.ReleaseAsync(reservations, VmState.Absent, "primary artifacts confirmed removed", ct);
            await vms.RemoveAsync(parent.Name, ct);
            await delegation.SaveCascadePreviewAsync(saved with { State = CascadeState.Completed, Outcomes = outcomes }, ct);
            await Phase("done");
            await Audit(true);
            return job.Kind == "remove-vm" ? new(new VmRemoveResult(parent.Name, removed)) :
                new(new { name = parent.Name, children = outcomes.Select(p => new { name = p.Key, outcome = p.Value }), releasedForwards = removed });
        }
        catch (Exception ex)
        {
            await delegation.SaveCascadePreviewAsync(saved with { State = CascadeState.Failed, Outcomes = outcomes }, CancellationToken.None);
            await Audit(false);
            throw new JobFailureException(SafeError.Describe(ex), new { name = parent.Name,
                children = outcomes.Select(p => new { name = p.Key, outcome = p.Value == "removed" ? "removed" : "retained", error = p.Value == "removed" ? null : p.Value }) });
        }
        async Task Phase(string phase) { await runner.SetPhaseAsync(job.Id, phase, ct); progress.Report("Cascade deletion: " + phase + "."); }
        Task Audit(bool success) => audit.AppendAsync(new(clock.UtcNow, job.Initiator ?? job.Owner, "vm.delete.completed", parent.Name,
            success ? AuditOutcome.Success : AuditOutcome.Failure, $"owner={parent.Owner}, parent={parent.Name}, job={job.Id}, cascade={preview.Children.Count}"), CancellationToken.None);
    }
    private async Task<JobOutcome> RemovePrimaryAsync(Job job, Vm parent, CascadePreview preview, IProgress<string> progress, CancellationToken ct)
    {
        try
        {
            var current = await vms.GetAsync(parent.Name, ct);
            if (current is not { Deleting: true, ChildCreationClosed: true } || parent.Incarnation is not null && current.Incarnation != parent.Incarnation)
                throw new LifecycleException("vm-incarnation-conflict");
            // Reuse the existing workflow, progress lines, result and success/failure audits.
            var result = await VmJobs.RemoveAsync(scopes, parent.Name, job.Initiator ?? job.Owner, progress, ct, async token =>
            {
                if (await driver.GetStateAsync(parent.Name, token) != VmState.Absent) throw new LifecycleException("cleanup-unverified");
                foreach (var reference in await media.ListReferencesForVmAsync(parent.Name, token))
                    await media.RemoveReferenceAsync(reference.MediaId, parent.Name, reference.Slot, token);
                await network.OnVmDeletedAsync(parent.Name, token);
                await delegation.RemoveOverrideAsync(parent.Name, token);
                var reservations = (await capacity.SnapshotAsync(false, token)).Reservations.Where(r => Ownership.SameName(r.VmName, parent.Name)).Select(r => r.Id).ToArray();
                await capacity.ReleaseAsync(reservations, VmState.Absent, "primary artifacts confirmed removed", token);
                await delegation.SaveCascadePreviewAsync(preview with { State = CascadeState.Completed, JobId = job.Id }, token);
            });
            return result;
        }
        catch
        {
            await delegation.SaveCascadePreviewAsync(preview with { State = CascadeState.Failed, JobId = job.Id }, CancellationToken.None);
            throw;
        }
    }
}
