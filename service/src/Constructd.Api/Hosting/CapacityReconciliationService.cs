using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
namespace Constructd.Api.Hosting;

/// <summary>Hosted services start after Bootstrap completes its interrupted-job and forwarding recovery.</summary>
public sealed class CapacityReconciliationService(ICapacityLedger ledger, IHostConfigStore config,
    ILogger<CapacityReconciliationService> logger, IMaintenanceGate? maintenance = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var mutation=maintenance?.TryEnter("mutation:capacity",Guid.NewGuid().ToString("n"),null);
                if(maintenance is null || mutation is not null) await ledger.ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("Capacity reconciliation failed; reservations remain retained."); }
            var seconds = HostAdminDefaults.Capacity.ReconcileSeconds;
            try { seconds = (await config.GetAsync<CapacityConfig>("capacity", stoppingToken))?.ReconcileSeconds ?? seconds; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("Capacity configuration could not be read."); }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds)), stoppingToken);
        }
    }
}
