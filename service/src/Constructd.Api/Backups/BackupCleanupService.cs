using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Api.Backups;

/// <summary>What one cleanup pass did.</summary>
public sealed record BackupCleanupResult(int Rotated, int StaleSessions, int OrphanFiles, int MarkedMissing, int Purged);

/// <summary>
/// Hourly housekeeping of config backups (docs/plans/host-config-backups.md §3.6): rotation for every
/// <em>(owner, VM)</em>, abandoned upload sessions (older than two hours, a slot session 30 minutes, nothing
/// writing) and orphaned partial files, and the bookkeeping of pairs whose VM is gone: <c>vm_missing_since</c> is set while
/// no VM of that owner and name exists and cleared when one does again; after
/// <c>RetainDeletedDays</c> the pair's backups are deleted. A pass is skipped during maintenance.
/// </summary>
public sealed class BackupCleanupService(BackupCoordinator backups, IVmRepository vms, IClock clock, IMaintenanceGate maintenance,
    ILogger<BackupCleanupService> logger) : BackgroundService
{
    public async Task<BackupCleanupResult?> RunAsync(CancellationToken ct)
    {
        using var activity = maintenance.TryEnter("backup-cleanup", Guid.NewGuid().ToString("n"), null);
        if (activity is null) return null;
        var store = backups.Store;
        var host = await backups.ConfigAsync(ct);
        int rotated = 0, stale = 0, orphans = 0, missing = 0, purged = 0;

        var pairs = (await store.ListVmsAsync(ct)).Select(s => (s.Owner, s.Vm))
            .Concat((await store.ListAllAsync(ct)).Select(b => (b.Owner, b.Vm)))
            .DistinctBy(p => (p.Owner.ToUpperInvariant(), p.Vm.ToUpperInvariant()))
            .ToList();
        foreach (var (owner, name) in pairs)
        {
            await using var held = await backups.LockAsync(owner, name, ct);
            var now = clock.UtcNow;
            var state = await store.GetVmAsync(owner, name, ct);
            var vm = await vms.GetAsync(name, ct);
            if (vm is null || !Ownership.SameName(vm.Owner, owner))
            {
                if (state?.VmMissingSince is { } since && now - since > TimeSpan.FromDays(host.RetainDeletedDays))
                {
                    foreach (var backup in await store.ListAsync(owner, name, ct)) await backups.DeleteLockedAsync(backup, ct);
                    await store.DeleteVmAsync(owner, name, ct);
                    purged++;
                    logger.LogInformation("Deleted the config backups of {Vm}: its VM has been gone for more than {Days} day(s).", name, host.RetainDeletedDays);
                    continue;
                }
                if (state?.VmMissingSince is null)
                {
                    state = (state ?? new BackupVmState(owner, name)) with { VmMissingSince = now };
                    await store.SaveVmAsync(state, ct);
                    missing++;
                }
            }
            else if (state?.VmMissingSince is not null)
            {
                state = state with { VmMissingSince = null };
                await store.SaveVmAsync(state, ct);
            }

            if (state?.RequestPendingAt is { } requested && now - requested >= BackupSchedule.RequestLifetime)
            {
                state = state with { RequestPendingAt = null, RequestTrigger = null };
                await store.SaveVmAsync(state, ct);
            }

            foreach (var session in (await store.ListAsync(owner, name, ct)).Where(b => backups.IsStale(b, now)))
            {
                await backups.DeleteLockedAsync(session, ct);
                stale++;
            }

            rotated += await backups.RotateLockedAsync(owner, name, host, state, ct);
        }

        // Files the database does not explain: a partial nothing is writing, or a payload without a row.
        // Listed before the rows are read, so a backup completing meanwhile is never mistaken for one.
        var entries = backups.Files.List();
        var known = (await store.ListAllAsync(ct)).Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (backups.IsUploading(entry.Id) || !entry.Partial && known.Contains(entry.Id)) continue;
            try
            {
                backups.Files.DeleteFile(entry);
                orphans++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not remove the orphaned backup file of {Id}: {Error}.", entry.Id, SafeError.Describe(ex));
            }
        }

        return new BackupCleanupResult(rotated, stale, orphans, missing, purged);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await RunAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning("Config backup cleanup failed ({Error}); it retries on the next hourly tick.", SafeError.Describe(ex));
            }
        }
    }
}
