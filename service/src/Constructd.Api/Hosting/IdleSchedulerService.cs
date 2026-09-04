using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Logic;
using Constructd.Core.Services;

namespace Constructd.Api.Hosting;

/// <summary>
/// Ticks the idle engine once a minute (plan §4.7 "a service scheduler evaluates policies each
/// minute"). <c>Constructd:Idle:SchedulerEnabled</c> turns that evaluation off; the tests do, and
/// call <see cref="IIdlePolicyEngine.EvaluateAsync"/> directly, so no test depends on wall clock
/// time.
///
/// It is also where the host's power availability request is reconciled (plan §4.13): this loop
/// already refreshes every VM's state from the hypervisor, so the second poller that would otherwise
/// be needed for "is anything still running" does not exist.
///
/// The two are switched INDEPENDENTLY. The composition root registers this service when either
/// responsibility is wanted, and the loop skips only the half that is off — so
/// <c>Idle:SchedulerEnabled=false</c> stops VMs being saved automatically and leaves
/// <c>Power:KeepHostAwake</c> doing its job, which is exactly how the two settings read.
/// </summary>
public sealed class IdleSchedulerService(
    IIdlePolicyEngine engine,
    HostPowerCoordinator power,
    IClock clock,
    ConstructdOptions options,
    ILogger<IdleSchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Idle.TickSeconds));

        logger.LogInformation(
            "Scheduler started (every {Interval}; idle evaluation {IdleState}, host power {PowerState}).",
            interval,
            options.Idle.SchedulerEnabled ? "on" : "off",
            options.Power.KeepHostAwake ? "on" : "off");

        // Before the first tick: a restart while VMs are running must take the request back
        // immediately, not a minute later.
        await ReconcilePowerAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(interval);

        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A bad tick must never take the service down — and must not log the raw exception.
                logger.LogError("Idle evaluation tick failed: {Error}.", SafeError.Describe(ex));
            }
        }
    }

    /// <summary>
    /// One tick: the idle evaluation, when <c>Idle:SchedulerEnabled</c> asks for it, and then the
    /// host power reconcile, which is switched independently. Public for the same reason
    /// <c>Idle:SchedulerEnabled</c> exists — a test drives a tick directly rather than waiting on a
    /// real timer.
    /// </summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        if (options.Idle.SchedulerEnabled)
        {
            var outcomes = await engine.EvaluateAsync(clock.UtcNow, cancellationToken).ConfigureAwait(false);

            foreach (var outcome in outcomes.Where(o => o.Applied || o.Error is not null))
            {
                if (outcome.Error is null)
                {
                    logger.LogInformation(
                        "Idle policy applied to {Vm}: {Decision} ({Reason}).",
                        outcome.VmName, outcome.Decision.Kind, outcome.Decision.Reason);
                }
                else
                {
                    // Safe description only: a rendered log entry would otherwise include the
                    // dependency's message, stack trace and Data.
                    logger.LogWarning(
                        "Idle evaluation of {Vm} failed: {Error}.", outcome.VmName, outcome.Error);
                }
            }
        }

        await ReconcilePowerAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Bring the host power request in line with what is running. It has its own try/catch so a
    /// failing platform call is reported as itself and never disturbs the idle work above it — and so
    /// the next tick simply tries again.
    /// </summary>
    private async Task ReconcilePowerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await power.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // Safe description only, like everywhere else: the raw exception may carry a dependency's
            // message and stack trace.
            logger.LogWarning("Host power reconcile failed: {Error}.", SafeError.Describe(ex));
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
