using Constructd.Core.Services;

namespace Constructd.Api.Repair;

/// <summary>
/// Follows the engine's change stream and hands every ended job to <see cref="RepairSessions"/>, which starts
/// the repair of a failed provision, reinstall or restore when the host allows it.
/// </summary>
public sealed class RepairTrigger(ChangeFeed changes, RepairSessions repairs, ILogger<RepairTrigger> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = changes.Subscribe(null);
        try
        {
            await foreach (var change in subscription.Live.ReadAllAsync(stoppingToken))
            {
                if (change.Type != "job.ended" || change.Vm is null) continue;
                var id = change.Id;
                _ = Task.Run(async () =>
                {
                    try { await repairs.OnJobEndedAsync(id, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        logger.LogWarning("Starting the repair after job {Job} failed: {Error}", id, Constructd.Core.Logic.SafeError.Describe(ex));
                    }
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>The engine stops: its repair sessions end with it (they are not kept across a restart).</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await repairs.EndAllAsync().WaitAsync(cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }
}
