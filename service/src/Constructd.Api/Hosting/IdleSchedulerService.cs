using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Logic;

namespace Constructd.Api.Hosting;

/// <summary>
/// Ticks the idle engine once a minute (plan §4.7 "a service scheduler evaluates policies each
/// minute"). Registered only when <c>Constructd:Idle:SchedulerEnabled</c> is true; the tests turn it
/// off and call <see cref="IIdlePolicyEngine.EvaluateAsync"/> directly, so no test depends on wall
/// clock time.
/// </summary>
public sealed class IdleSchedulerService(
    IIdlePolicyEngine engine,
    IClock clock,
    ConstructdOptions options,
    ILogger<IdleSchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Idle.TickSeconds));
        logger.LogInformation("Idle scheduler started (every {Interval}).", interval);

        using var timer = new PeriodicTimer(interval);

        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var outcomes = await engine.EvaluateAsync(clock.UtcNow, stoppingToken).ConfigureAwait(false);

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
