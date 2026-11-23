namespace Constructd.Api.Hosting;

/// <summary>Runs the engine's host until it stops (what <c>app.RunAsync()</c> does), with one difference below.</summary>
public static class EngineRun
{
    /// <summary>How long the host lifetime may take to finish a stop that ran past the shutdown timeout.</summary>
    public static readonly TimeSpan LifetimeStop = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A stop that runs past the host's shutdown timeout ends here with a warning instead of an unhandled
    /// exception. The Windows service lifetime throws <see cref="OperationCanceledException"/> for it before it
    /// tells the service manager the service stopped, and Windows records the exception as a crash. Here the
    /// lifetime gets to finish its stop without the expired token, so the service still ends as stopped.
    /// </summary>
    public static async Task RunAsync(IHost host, ILogger logger)
    {
        var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        try
        {
            // A start that fails, or that a failing background service cancels, still fails the run.
            await host.StartAsync().ConfigureAwait(false);
            try
            {
                await host.WaitForShutdownAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                logger.LogWarning("The engine's stop ran past its shutdown timeout; requests and work still running were cut off.");
                try
                {
                    await host.Services.GetRequiredService<IHostLifetime>().StopAsync(CancellationToken.None).WaitAsync(LifetimeStop).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning("The host lifetime did not finish its stop: {Error}", Core.Logic.SafeError.Describe(ex));
                }
            }
        }
        finally
        {
            if (host is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(false);
            else host.Dispose();
        }
    }
}
