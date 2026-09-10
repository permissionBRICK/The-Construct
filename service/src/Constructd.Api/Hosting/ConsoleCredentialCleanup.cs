using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;

namespace Constructd.Api.Hosting;

public sealed class ConsoleCredentialCleanup(IInteractiveConsole console, ConstructdOptions options,
    ILogger<ConsoleCredentialCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.BrowserConsoleEnabled || options.Fake) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try { await console.ReconcileAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch { logger.LogWarning("Browser console credential cleanup will retry."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
