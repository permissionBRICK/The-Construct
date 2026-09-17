using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
namespace Constructd.Api.Hosting;

public sealed class TokenUsageCleanupService(ITokenUsageStore usage, IHostConfigStore config, IClock clock,
    IMaintenanceGate maintenance, ILogger<TokenUsageCleanupService> logger) : BackgroundService
{
    public async Task PruneAsync(CancellationToken ct)
    {
        using var activity = maintenance.TryEnter("usage-cleanup", Guid.NewGuid().ToString("n"), null);
        if (activity is null) return;
        var policy = await config.GetAsync<UsageConfig>("usage", ct) ?? HostAdminDefaults.Usage;
        await usage.PruneAsync(DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(-policy.RetentionDays), ct);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try { await PruneAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch { logger.LogWarning("Token usage cleanup failed; it will retry on the next daily tick."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
