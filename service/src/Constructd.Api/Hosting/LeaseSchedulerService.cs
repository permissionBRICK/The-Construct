using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Hosting;

/// <summary>Expiry selection uses persisted deadlines; no hypervisor probes happen in this scan.</summary>
public sealed class LeaseSchedulerService(IVmRepository vms, IVmDelegationRepository delegation,
    IVmOperationGate gates, IHostConfigStore config, LifecycleJobAdmission jobs,
    IMaintenanceGate maintenance, IJobStore jobStore, IClock clock,
    IConfiguration settings, ILogger<LeaseSchedulerService> logger) : BackgroundService
{
    private readonly SemaphoreSlim tick = new(1, 1);
    public async Task<IReadOnlyList<string>> TickAsync(CancellationToken ct)
    {
        await tick.WaitAsync(ct);
        try
        {
            using var mutation = maintenance.TryEnter("mutation:lease", Guid.NewGuid().ToString("n"), null);
            if (mutation is null) return [];
            var policy = await config.GetAsync<LifecycleConfig>("lifecycle", ct) ?? HostAdminDefaults.Lifecycle;
            var submitted = new List<string>();
            foreach (var listed in await delegation.ListLeasesDueAsync(clock.UtcNow, TimeSpan.FromSeconds(policy.LeaseRetrySeconds), ct))
            {
                try
                {
                    await using var handle = await gates.TryAcquireAsync(listed.Name, "lease-tick", ct);
                    if (handle is null) continue;
                    var vm = await vms.GetAsync(listed.Name, ct);
                    if (vm is null || vm.Deleting || !LeaseRules.RetryDue(vm.Lease, clock.UtcNow, TimeSpan.FromSeconds(policy.LeaseRetrySeconds)) ||
                        vm.CurrentJobId is string jobId && await jobStore.GetAsync(jobId, ct) is { State: JobState.Queued or JobState.Running }) continue;
                    submitted.Add((await jobs.SubmitAsync(vm, "system", false, vm.Lease!.Version, null, ct)).Id);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { logger.LogWarning("Child expiry could not be submitted; its persisted deadline is retained."); }
            }
            return submitted;
        }
        finally { tick.Release(); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.GetValue("Constructd:Lease:SchedulerEnabled", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var seconds = HostAdminDefaults.Lifecycle.LeaseTickSeconds;
            try
            {
                await TickAsync(stoppingToken);
                seconds = (await config.GetAsync<LifecycleConfig>("lifecycle", stoppingToken))?.LeaseTickSeconds ?? seconds;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch { logger.LogWarning("Child lease tick failed; persisted deadlines and capacity are retained."); }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds)), stoppingToken);
        }
    }
}
