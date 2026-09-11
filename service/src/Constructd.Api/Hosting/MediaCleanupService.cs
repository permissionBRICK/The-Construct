using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
namespace Constructd.Api.Hosting;
public sealed class MediaCleanupService(MediaJobs work,IMaintenanceGate maintenance,IJobEngine jobs,IOperationKeyStore keys, Constructd.Api.Source.ISourceCache source) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        using(var activity=maintenance.TryEnter("media-recovery","startup",null))
            if(activity is not null) await work.RecoverAsync(cancellationToken);
        await source.RecoverAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromHours(24));
        while(await timer.WaitForNextTickAsync(stoppingToken))
        {
            var activity=maintenance.TryEnter("media-cleanup",Guid.NewGuid().ToString("n"),null);
            if (activity is not null)
            {
                try
                {
                    await jobs.SubmitAsync("media-cleanup",null,"system",async(p,ct)=>
                    { using(activity) {var result=await work.CleanupAsync("system",p,ct); await keys.SweepAsync(DateTimeOffset.UtcNow.AddHours(-24),ct); return result;} },stoppingToken);
                }
                catch { activity.Dispose(); }
            }
            var sourceActivity = maintenance.TryEnter("source-cleanup", Guid.NewGuid().ToString("n"), null);
            if (sourceActivity is null) continue;
            try
            {
                await jobs.SubmitAsync("source-cleanup", null, "system", async (p, ct) =>
                { using (sourceActivity) return new Constructd.Core.Domain.JobOutcome(await source.PruneAsync("system", p, ct)); }, stoppingToken);
            }
            catch { sourceActivity.Dispose(); }
        }
    }
}
