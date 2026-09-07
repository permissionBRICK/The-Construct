using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Hosting;

// Resolve the start coordinator at invocation time: it shares the capacity ledger whose
// reconciliation callback invokes this service. Construction must not create a DI cycle.
public sealed class ChildLeaseReconciler(IServiceProvider services, ILogger<ChildLeaseReconciler> logger) : IChildLeaseReconciler
{
    public async Task ReconcileAsync(Vm listed, VmState state, CancellationToken ct)
    {
        if (listed.Kind != VmKind.Child || listed.Deleting || state != VmState.Running) return;
        var vms = services.GetRequiredService<IVmRepository>();
        var vm = await vms.GetAsync(listed.Name, ct);
        if (vm is null || vm.Deleting || vm.Lease is null || vm.CurrentJobId is string jobId &&
            await services.GetRequiredService<IJobStore>().GetAsync(jobId, ct) is { State: JobState.Queued or JobState.Running }) return;
        try
        {
            var intent = (await services.GetRequiredService<IOperationKeyStore>().ListInFlightAsync(vm.Name, ct))
                .FirstOrDefault(k => k.PowerGeneration == vm.PowerGeneration && k.Kind is "lifecycle-start" or "restart-start" or "child-start");
            if (intent is not null)
            {
                if (intent.Kind is "lifecycle-start" or "restart-start") await services.GetRequiredService<LifecycleStart>().CompleteAsync(vm, intent, state, ct);
                else if (intent.JobId is not null && await services.GetRequiredService<IJobStore>().GetAsync(intent.JobId, ct) is { } original)
                    await services.GetRequiredService<ChildStartIntent>().RunAsync(original, vm,
                        (await services.GetRequiredService<ICapacityLedger>().SnapshotAsync(false, ct)).Reservations.Where(r => Ownership.SameName(r.VmName, vm.Name)).Select(r => r.Id).ToArray(), ct);
                return;
            }
            if (vm.Lease.State is not (LeaseState.Inactive or LeaseState.Expired)) return;
            var now = services.GetRequiredService<IClock>().UtcNow;
            var lease = vm.Lease with { State = LeaseState.Overdue, ExpiresAt = now,
                ActivatedAt = vm.Lease.ActivatedAt ?? now, LastExpiryOutcome = "external-start", LastExpiryAttemptAt = null, Version = vm.Lease.Version + 1 };
            await services.GetRequiredService<IAdmissionStore>().MutateAsync(null, async scope =>
            {
                if (vm.State != state && !await scope.UpdatePowerStateAsync(vm.Name, state, vm.PowerGeneration)) return false;
                return await scope.UpdateLeaseAsync(vm.Name, lease, vm.Lease.Version);
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { logger.LogWarning("Child lease reconciliation remains pending; its original deadline is retained."); }
    }
}
