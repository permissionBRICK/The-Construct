using Constructd.Api.Jobs;
namespace Constructd.Api.Hosting;

/// <summary>
/// Brings every primary VM's minimum RAM in line once the service is up (Proxmox): after an upgrade that
/// introduced it, after a host-default change while the service was down, and for VMs saved at the time.
/// Waits first so the node's own onboot starts are not competing for the VM configuration locks.
/// </summary>
public sealed class MemoryFloorStartupService(PrimaryMemoryFloor floor, ILogger<MemoryFloorStartupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            await floor.ApplyAllAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogWarning(ex, "Applying the minimum RAM to primary VMs at startup failed; each VM's next start applies it."); }
    }
}
