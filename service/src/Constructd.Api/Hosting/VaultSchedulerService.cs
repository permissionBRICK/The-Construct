using Constructd.Core.Logic;
using Constructd.Core.Services;

namespace Constructd.Api.Hosting;

/// <summary>
/// Drives the hosted key vault: every 10 s it expires leases and approvals and schedules scrubs; every
/// 30 s it drops the unlocked key of any VM that is no longer observed running. Tests turn it off
/// (<c>Constructd:Vault:SchedulerEnabled=false</c>) and call the service directly.
/// </summary>
public sealed class VaultSchedulerService(VaultHostService vault, IConfiguration settings, ILogger<VaultSchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.GetValue("Constructd:Vault:SchedulerEnabled", true)) return;
        var lastStateCheck = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await vault.TickAsync(stoppingToken).ConfigureAwait(false);
                if (DateTimeOffset.UtcNow - lastStateCheck >= VaultHostService.StateCheck)
                {
                    lastStateCheck = DateTimeOffset.UtcNow;
                    await vault.CheckUnlockedAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // A safe description only: a store or driver exception may carry anything in its message.
                logger.LogWarning("Key vault tick failed: {Error}.", SafeError.Describe(ex));
            }

            try { await Task.Delay(VaultHostService.Tick, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
