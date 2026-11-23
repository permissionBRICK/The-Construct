using Constructd.Api.Jobs;
namespace Constructd.Api.Hosting;

/// <summary>
/// Sets once per engine start which VMs the host brings back at its boot (<see cref="HostStartSettings"/>):
/// VMs made or adopted before the rule, and primaries whose effective idle policy changed with the host's
/// idle settings while the engine was down. Waits first so the host's own boot starts are not competing for
/// the VM configuration locks.
/// </summary>
public sealed class HostStartStartupService(HostStartSettings settings, ILogger<HostStartStartupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            await settings.ApplyAllAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogWarning(ex, "Setting which VMs start with the host failed at startup; the next engine start tries again."); }
    }
}
