namespace Constructd.Api.Profiles;

/// <summary>Runs <see cref="ProfileSync.SyncAsync"/> every <see cref="ProfileSync.Interval"/> and when woken.</summary>
public sealed class ProfileSyncService(ProfileSync sync, ILogger<ProfileSyncService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await sync.SyncAsync(stoppingToken);
                await sync.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError("The profile sync pass failed: {Reason}", ex.Message);
                await Task.Delay(ProfileSync.Interval, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }
}
