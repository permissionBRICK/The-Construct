using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Windows.Updates;

namespace Constructd.Api.Hosting;

/// <summary>
/// The host's side of "automatic" (12.1): with <c>updates.auto</c> on, once an hour it runs the one update
/// action when the channel offers a release this host does not run. A release that already failed here is
/// not tried again by itself; the admin runs it by hand. After an update succeeded, the VMs that follow it
/// are reprovisioned (<see cref="VmUpdates.FollowAsync"/>).
/// </summary>
public sealed class HostAutoUpdateService(HostUpdateJob work, IHostUpdateStore store, IReleaseInfo release, IMaintenanceGate gate,
    PackageStager checker, VmUpdates vms, IClock clock, ILogger<HostAutoUpdateService> logger) : BackgroundService
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);
    private DateTimeOffset _nextCheck = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try { do await TickAsync(ct); while (await timer.WaitForNextTickAsync(ct)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>One pass. Answers the update it started, if any.</summary>
    public async Task<object?> TickAsync(CancellationToken ct, bool checkNow = false)
    {
        try
        {
            if (gate.State != MaintenanceState.Open) return null;
            var last = (await store.ListAsync(1, ct)).FirstOrDefault();
            if (last is { State: HostUpdateState.Succeeded } && last.Commit == release.Installed.Commit) await vms.FollowAsync(last, ct);
            if (!checkNow && clock.UtcNow < _nextCheck) return null;
            _nextCheck = clock.UtcNow + CheckEvery;
            if (!(await checker.SettingsAsync(ct)).Auto || await store.GetActiveAsync(ct) is not null) return null;
            var offer = await work.ReadChannelAsync(ct);
            if (offer.Release.Commit == release.Installed.Commit || last?.Commit == offer.Release.Commit) return null;
            await work.Acceptance.WaitAsync(ct);
            try { return await work.RunAsync("auto-update", ct); }
            finally { work.Acceptance.Release(); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Automatic host update check failed ({Error}).", Constructd.Core.Logic.SafeError.Describe(ex));
            return null;
        }
    }
}
