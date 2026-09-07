using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Jobs;

public sealed class ChildCreateJob(IVmRepository vms, IVmMetadataStore identity,
    IChildVmDriver driver, IChildVmCreationOwnership ownership, IHypervisorDriver hypervisor, ICapacityLedger capacity, IMediaStore media,
    IVmOperationGate vmGate, IGuestAddressProvider addresses, INetworkPolicyReconciler network,
    IPersistedJobRunner runner, IAdmissionStore admission, IOperationKeyStore keys, ChildStartIntent starter, ChildDeleteJob cleanup, IAuditLog audit, IClock clock, ConstructdOptions options)
{
    public async Task<JobOutcome> RunAsync(Job job, Vm vm, ChildStoragePlacement placement, bool start,
        IReadOnlyList<string> reservationIds, IProgress<string> progress, CancellationToken ct)
    {
        await using var parentGate = await vmGate.AcquireAsync(vm.Parent!, job.Id, ct);
        await using var childGate = await vmGate.AcquireAsync(vm.Name, job.Id, ct);
        var allocationAttempted = false;
        var startIntentExists = await keys.GetAsync(vm.Owner, "child-start", job.Id + ":start", ct) is not null;
        var recoveringStart = startIntentExists;
        try
        {
            await Phase("admit");
            var parent = await vms.GetAsync(vm.Parent!, ct);
            if (parent is not { Kind: VmKind.Primary, Deleting: false, ChildCreationClosed: false }) throw new ChildValidationException("parent-closed", "parent");
            await capacity.ExtendAsync(reservationIds, TimeSpan.FromHours(2), ct);
            if (!startIntentExists)
            {
                await Phase("media");
                string? install = null, auxiliary = null;
                foreach (var reference in await media.ListReferencesForVmAsync(vm.Name, ct))
                {
                    var item = await media.GetAsync(reference.MediaId, ct);
                    if (item is not { State: MediaState.Ready }) throw new ChildValidationException("media-not-ready", "media");
                    if (reference.Slot == MediaSlot.Install) install = item.Path; else auxiliary = item.Path;
                }
                if (install is null) throw new ChildValidationException("media-not-ready", "installMediaId");
                await Phase("hardware");
                HardwarePresets.ValidateCapabilities(vm.Hardware!, await driver.GetCapabilitiesAsync(ct), auxiliary is not null);
                // Any create attempt may allocate before reporting failure. Cleanup uses its ownership marker.
                allocationAttempted = true;
                await ownership.CreateOwnedAsync(new(vm.Name, vm.Hardware!, placement.DiskPath, install, auxiliary, options.SwitchName), job.Id, progress, ct);
                var id = await driver.GetVmIdAsync(vm.Name, ct) ?? throw new ChildValidationException("cleanup-unverified", "incarnation");
                if (!await identity.UpdateIncarnationAsync(vm.Name, id, ct)) throw new ChildValidationException("vm-deleting", "vm");
                vm = vm with { Incarnation = id };
                await Phase("disk");
                await Phase("attach");
                var attached = await driver.GetAttachedMediaAsync(vm.Name, ct);
                if (!attached.Complete || !string.Equals(attached.InstallPath, install, StringComparison.OrdinalIgnoreCase) || !string.Equals(attached.AuxiliaryPath, auxiliary, StringComparison.OrdinalIgnoreCase)) throw new ChildValidationException("media-unverified", "media");
            }
            else
            {
                vm = await vms.GetAsync(vm.Name, ct) ?? throw new ChildValidationException("vm-deleting", "vm");
                if (vm.Incarnation != await driver.GetVmIdAsync(vm.Name, ct) || await ownership.GetCreationOperationAsync(vm.Name, ct) != job.Id) throw new ChildValidationException("vm-incarnation-conflict", "vm");
            }
            var state = await hypervisor.GetStateAsync(vm.Name, ct);
            if (start)
            {
                await Phase("start");
                // Once accepted, the intent owns recovery; ordinary failure cleanup must not
                // destroy a running VM or erase an intervening power decision.
                startIntentExists = true;
                vm = await starter.RunAsync(job, vm, reservationIds, ct);
                state = vm.State;
            }
            else
            {
                if (state != VmState.Off) throw new ChildValidationException("create-state-unverified", "state");
                var confirmed = await admission.MutateAsync(null, async scope =>
                {
                    await scope.ConfirmReservationsAsync(reservationIds, state);
                    return true;
                }, ct);
                if (confirmed.Outcome != AdmissionOutcome.Accepted) throw new ChildValidationException("operation-key-conflict", "vm");
            }
            var lease = vm.Lease!;
            vm = vm with { State = state, Lease = lease };
            await vms.UpdateAsync(vm, ct);
            await network.OnVmCreatedAsync(vm, ct);
            IReadOnlyList<GuestAddress> reported = [];
            try { reported = await addresses.GetReportedAddressesAsync(vm.Name, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { progress.Report("Guest addresses are unavailable; no installation status is inferred."); }
            await Phase("done");
            await Audit("succeeded");
            return new(new { name = vm.Name, parent = vm.Parent, incarnation = vm.Incarnation, state, lease, addresses = reported.Select(a => a with { Verified = false }).ToArray() });
        }
        catch
        {
            try
            {
                if (startIntentExists && !recoveringStart && await hypervisor.GetStateAsync(vm.Name, CancellationToken.None) == VmState.Off)
                {
                    await keys.CompleteAsync(vm.Owner, "child-start", job.Id + ":start", "\"start-failed\"", CancellationToken.None);
                    startIntentExists = false;
                }
                if (startIntentExists)
                {
                    progress.Report("Start intent retained for reconciliation; ownership and capacity remain recorded.");
                }
                else if (allocationAttempted)
                {
                    if (await ownership.GetCreationOperationAsync(vm.Name, CancellationToken.None) != job.Id)
                        throw new ChildValidationException("artifact-ownership-unverified", "vm");
                    await cleanup.CleanupAsync(vm, progress, null, CancellationToken.None, deleteDedicated: false);
                }
                else
                {
                    foreach (var reference in await media.ListReferencesForVmAsync(vm.Name, CancellationToken.None)) await media.RemoveReferenceAsync(reference.MediaId, vm.Name, reference.Slot, CancellationToken.None);
                    await capacity.ReleaseAsync(reservationIds, VmState.Absent, "no allocation attempted", CancellationToken.None);
                    await vms.RemoveAsync(vm.Name, CancellationToken.None);
                }
            }
            catch { progress.Report("Cleanup remains incomplete; VM ownership, media references and capacity are retained for retry."); }
            await Audit("failed");
            throw;
        }
        async Task Phase(string phase) { await runner.SetPhaseAsync(job.Id, phase, ct); progress.Report("Child creation: " + phase + "."); }
        Task Audit(string outcome) => audit.AppendAsync(new(clock.UtcNow, job.Initiator ?? job.Owner, "child.create", vm.Name, outcome == "succeeded" ? AuditOutcome.Success : AuditOutcome.Failure, $"op=child.create, owner={vm.Owner}, parent={vm.Parent}, target={vm.Name}, job={job.Id}"), CancellationToken.None);
    }
}
