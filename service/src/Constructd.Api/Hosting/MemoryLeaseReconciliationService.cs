using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
namespace Constructd.Api.Hosting;

/// <summary>Memory mode has no capacity reconciler; use its configured cadence for lease recovery.</summary>
public sealed class MemoryLeaseReconciliationService(IVmRepository vms, IHypervisorDriver driver,
    IVmOperationGate gates, IChildLeaseReconciler leases, IHostConfigStore config, IMaintenanceGate maintenance,
    IConfiguration settings, ILogger<MemoryLeaseReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!settings.GetValue("Constructd:Lease:SchedulerEnabled", true)) return;
        while (!ct.IsCancellationRequested)
        {
            var seconds = HostAdminDefaults.Capacity.ReconcileSeconds;
            try
            {
                using var mutation = maintenance.TryEnter("mutation:lease-reconcile", Guid.NewGuid().ToString("n"), null);
                if (mutation is not null)
                    foreach (var vm in (await vms.ListAsync(null, ct)).Where(v => v.Kind == VmKind.Child && !v.Deleting))
                    {
                        try
                        {
                            await using var held = await gates.TryAcquireAsync(vm.Name, "lease-reconcile", ct);
                            if (held is not null) await leases.ReconcileAsync(vm, await driver.GetStateAsync(vm.Name, ct), ct);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch { logger.LogWarning("Child lease observation unavailable."); }
                    }
                seconds = (await config.GetAsync<CapacityConfig>("capacity", ct))?.ReconcileSeconds ?? seconds;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { logger.LogWarning("Child lease reconciliation failed; retrying at the configured interval."); }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds)), ct);
        }
    }
}
