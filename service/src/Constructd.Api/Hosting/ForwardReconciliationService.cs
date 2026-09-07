using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Logic;

namespace Constructd.Api.Hosting;

/// <summary>
/// Startup can precede Hyper-V guest networking. Retry after boot, and keep following address
/// changes from guest reboots or starts outside the API, independently of idle/power settings.
/// </summary>
public sealed class ForwardReconciliationService(
    IPortForwardManager forwards,
    ConstructdOptions options,
    ILogger<ForwardReconciliationService> logger, IMaintenanceGate? maintenance = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Bootstrap already made the initial pass and reserved the persisted allocations.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.ForwardReconcileSeconds)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    /// <summary>A single retry, also callable by tests without waiting on wall clock time.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        using var mutation = maintenance?.TryEnter("mutation:scheduler", Guid.NewGuid().ToString("n"), null);
        if (maintenance is not null && mutation is null) return;
        try
        {
            var repaired = await forwards.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            if (repaired > 0)
            {
                logger.LogInformation("Reconciled {Count} host port forward(s).", repaired);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A transient netsh/Hyper-V failure must not stop the API or prevent the next retry.
            logger.LogWarning("Host forward reconcile failed: {Error}.", SafeError.Describe(ex));
        }
    }
}
