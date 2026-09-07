using System.Text.Json;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Jobs;

public sealed class ChildLifecycleJobs(IVmRepository vms, IHypervisorDriver hypervisor, IChildVmDriver driver,
    IVmOperationGate gates, IAdmissionStore admission, ICapacityLedger capacity, IHostConfigStore config,
    IPersistedJobRunner runner, IOperationKeyStore keys, LifecycleStart starts, IAuditLog audit, IClock clock)
{
    public async Task<JobOutcome> RunAsync(Job job, bool restart, long? expectedLeaseVersion, IProgress<string> progress, CancellationToken ct)
    {
        await using var gate = await gates.AcquireAsync(job.VmName!, job.Id, ct);
        var vm = await vms.GetAsync(job.VmName!, ct);
        if (vm is null || vm.Deleting || expectedLeaseVersion is long expected &&
            (vm.Lease?.Version != expected || !LeaseRules.Due(vm.Lease, clock.UtcNow)))
            return new(new { name = job.VmName, outcome = "superseded", finalState = vm?.State });
        var expiry = expectedLeaseVersion is not null;
        var state = VmState.Unknown;
        var outcome = "completed";
        try
        {
            state = await hypervisor.GetStateAsync(vm.Name, ct);
            if (restart && await keys.GetAsync(vm.Owner, "restart-start", job.Id + ":start", ct) is { } recovery)
            {
                var reply = await starts.RunAsync(vm, null, null, recovery, ct);
                if (reply.Code is not null) throw new LifecycleException(reply.Code);
                state = reply.State;
                await Phase("done"); await Audit(true);
                return new(new { name = vm.Name, outcome = "completed", finalState = state });
            }
            if (restart && (LeaseRules.Due(vm.Lease, clock.UtcNow) || vm.Lease?.State == LeaseState.Overdue)) throw new LifecycleException("lease-due");
            await Phase(restart ? "shutdown" : "request");
            if (state is not (VmState.Off or VmState.Saved or VmState.Absent))
            {
                var settings = await config.GetAsync<LifecycleConfig>("lifecycle", ct) ?? HostAdminDefaults.Lifecycle;
                var shutdown = state == VmState.Paused ? GracefulShutdownOutcome.Unavailable :
                    await driver.ShutdownGracefulAsync(vm.Name, TimeSpan.FromSeconds(settings.GracefulShutdownTimeoutSeconds), progress, ct);
                await Phase("wait");
                state = await hypervisor.GetStateAsync(vm.Name, ct);
                if (shutdown != GracefulShutdownOutcome.Completed || state != VmState.Off)
                {
                    outcome = shutdown == GracefulShutdownOutcome.Unavailable ? "unavailable" : shutdown == GracefulShutdownOutcome.Timeout ? "timeout" : "failed";
                    throw new LifecycleException(shutdown == GracefulShutdownOutcome.Unavailable ? "guest-shutdown-unavailable" : shutdown == GracefulShutdownOutcome.Timeout ? "shutdown-timeout" : "shutdown-failed");
                }
            }
            else outcome = "already-off";
            await PersistState(vm, state, expiry ? LeaseRules.ExpiryOutcome(vm.Lease!, clock.UtcNow, true, outcome) : null, !restart, ct);
            vm = (await vms.GetAsync(vm.Name, ct))!;
            if (restart)
            {
                if (state != VmState.Off || LeaseRules.Due(vm.Lease, clock.UtcNow)) throw new LifecycleException("lease-due");
                await Phase("start");
                var proposed = new OperationKeyRecord(vm.Owner, "restart-start", job.Id + ":start", job.Id, vm.Name, job.Id,
                    OperationKeyState.InFlight, "{\"restart\":true}", vm.PowerGeneration, null, clock.UtcNow);
                var reply = await starts.RunAsync(vm, null, null, proposed, ct);
                if (reply.Code is not null) throw new LifecycleException(reply.Code);
                state = reply.State;
                // No lease write on restart, even if shutdown took most of the remaining lifetime.
                outcome = "completed";
            }
            await Phase("done");
            await Audit(true);
            return new(new { name = vm.Name, outcome = outcome == "already-off" ? "completed" : outcome, finalState = state });
        }
        catch (Exception ex)
        {
            var code = ex is LifecycleException failure ? failure.Code : SafeError.Describe(ex);
            try
            {
                vm = (await vms.GetAsync(vm.Name, CancellationToken.None))!;
                state = await hypervisor.GetStateAsync(vm.Name, CancellationToken.None);
                // A dispatched start may have reached the hypervisor before persistence failed.
                // Keep its generation so the scheduler can finish that exact durable intent.
                var pendingStart = restart && (await keys.GetAsync(vm.Owner, "restart-start", job.Id + ":start", CancellationToken.None)) is { State: OperationKeyState.InFlight };
                if (!pendingStart) await PersistState(vm, state, expiry ? LeaseRules.ExpiryOutcome(vm.Lease!, clock.UtcNow, false,
                    code == "guest-shutdown-unavailable" ? "unavailable" : code == "shutdown-timeout" ? "timeout" : "failed") : null, true, CancellationToken.None);
            }
            catch { /* No observation or failed persistence: all liabilities remain for reconciliation. */ }
            await Audit(false);
            throw new JobFailureException(code, new { name = job.VmName, outcome = code == "guest-shutdown-unavailable" ? "unavailable" : code == "shutdown-timeout" ? "timeout" : "failed", finalState = state, code });
        }
        async Task Phase(string phase) { await runner.SetPhaseAsync(job.Id, phase, ct); progress.Report("VM lifecycle: " + phase + "."); }
        Task Audit(bool success) => audit.AppendAsync(new(clock.UtcNow, expiry ? "system" : job.Initiator ?? job.Owner,
            expiry ? "vm.lease.expired" : "vm.lifecycle", job.VmName!, success ? AuditOutcome.Success : AuditOutcome.Failure,
            $"op={(restart ? "restart" : "shutdown")}, owner={job.Owner}, initiator={job.Initiator ?? job.Owner}, target={job.VmName}, job={job.Id}, reason={(expiry ? "lease-expiry" : "user")}"), CancellationToken.None);
    }

    public async Task PersistState(Vm vm, VmState state, Lease? lease, bool release, CancellationToken ct)
    {
        var rows = (await capacity.SnapshotAsync(false, ct)).Reservations.Where(r => Ownership.SameName(r.VmName, vm.Name) &&
            (r.Resource != ReservationResource.Storage || ReservationRules.SavedState(r))).Select(r => r.Id).ToArray();
        var result = await admission.MutateAsync(null, async scope =>
        {
            if (state != VmState.Unknown && !await scope.UpdatePowerStateAsync(vm.Name, state, vm.PowerGeneration)) return false;
            if (lease is not null && !await scope.UpdateLeaseAsync(vm.Name, lease, vm.Lease!.Version)) return false;
            if (release) await scope.ReleaseReservationsAsync(rows, state, "observed-lifecycle-completion");
            return true;
        }, ct);
        if (result.Outcome != AdmissionOutcome.Accepted) throw new LifecycleException("power-state-changed");
    }
}
