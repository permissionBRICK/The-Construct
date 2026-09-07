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
    ILogger<ForwardReconciliationService> logger, IMaintenanceGate? maintenance = null, Constructd.Core.Services.AccessExposure? exposure = null,
    INetworkPolicyReconciler? network = null, IGuestAddressProvider? addresses = null, IVmRepository? vms = null) : BackgroundService
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
        // Primary repair goes first and has an independent failure boundary.
        await RunPassAsync("Host forward", async () =>
        {
            var repaired = await forwards.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            if (repaired > 0) logger.LogInformation("Reconciled {Count} host port forward(s).", repaired);
        }, cancellationToken);
        IGuestAddressProvider? snapshot = addresses is IGuestAddressSnapshotProvider ? null : addresses;
        await RunPassAsync("Guest network snapshot", async () =>
        {
            if (addresses is IGuestAddressSnapshotProvider snapshots && vms is not null &&
                (await vms.ListAsync(null, cancellationToken)).Any(v => v.Kind == Constructd.Core.Domain.VmKind.Child))
                snapshot = await snapshots.CaptureAsync(cancellationToken);
        }, cancellationToken);
        if (exposure is not null)
            await RunPassAsync("Child forward", async () => await exposure.ReconcileAsync(cancellationToken, snapshot), cancellationToken);
        if (network is not null)
            await RunPassAsync("Network policy", async () =>
            {
                if (snapshot is null) await network.ReconcileAsync(cancellationToken);
                else await network.ReconcileAsync(snapshot, cancellationToken);
            }, cancellationToken);
    }

    private async Task RunPassAsync(string pass, Func<Task> action, CancellationToken ct)
    {
        try { await action(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning("{Pass} reconcile failed: {Error}.", pass, SafeError.Describe(ex)); }
    }
}
