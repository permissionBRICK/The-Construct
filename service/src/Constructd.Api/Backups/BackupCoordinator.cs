using System.Collections.Concurrent;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;

namespace Constructd.Api.Backups;

/// <summary>
/// The host side of config backups (docs/plans/host-config-backups.md §3.4–§3.7) shared by the routes,
/// the heartbeat and the hourly cleanup: one in-process lock per <em>(owner, VM)</em> serialises every
/// write to a pair, and the uploads in flight are tracked so cleanup never removes a file being written.
/// </summary>
public sealed class BackupCoordinator(IBackupStore store, IBackupFiles files, IHostConfigStore config, IClock clock,
    IMaintenanceGate maintenance, ILogger<BackupCoordinator> logger)
{
    private readonly InMemoryVmOperationGate _locks = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _uploads = new(StringComparer.Ordinal);

    /// <summary>How often an upload re-checks the host's free space (64 MiB; tests lower it).</summary>
    public long SpaceCheckBytes { get; set; } = 64L << 20;

    public IBackupStore Store => store;
    public IBackupFiles Files => files;

    public async Task<BackupsConfig> ConfigAsync(CancellationToken ct) =>
        await config.GetAsync<BackupsConfig>("backups", ct).ConfigureAwait(false) ?? HostAdminDefaults.Backups;

    public Task<IAsyncDisposable> LockAsync(string owner, string vm, CancellationToken ct) =>
        _locks.AcquireAsync(owner.ToLowerInvariant() + "\n" + vm.ToLowerInvariant(), "backup", ct);

    public static bool Belongs(ConfigBackup backup, Vm vm) =>
        Ownership.SameName(backup.Owner, vm.Owner) && Ownership.SameName(backup.Vm, vm.Name);

    public bool IsUploading(string id) => _uploads.ContainsKey(id);

    /// <summary>An open session past <see cref="BackupSchedule.StaleSession"/> that nothing is writing.</summary>
    public bool IsStale(ConfigBackup backup, DateTimeOffset now) =>
        backup.IsOpen && now - backup.CreatedAt >= BackupSchedule.StaleSession && !IsUploading(backup.Id);

    /// <summary>Registers an upload; the token is cancelled when the session is deleted under it. Null when one is running.</summary>
    public CancellationTokenSource? BeginUpload(string id, CancellationToken requestAborted)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        if (_uploads.TryAdd(id, source)) return source;
        source.Dispose();
        return null;
    }

    /// <summary>Ends the upload registered as <paramref name="source"/>; a later upload of the same id is left alone.</summary>
    public void EndUpload(string id, CancellationTokenSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_uploads.TryRemove(new KeyValuePair<string, CancellationTokenSource>(id, source))) source.Dispose();
    }

    /// <summary>The heartbeat's question (§3.6 "Due"), primaries only; never while the host drains for maintenance.</summary>
    public async Task<BackupDecision> HeartbeatAsync(Vm vm, bool busy, CancellationToken ct)
    {
        if (vm.Kind != VmKind.Primary || maintenance.State != MaintenanceState.Open) return BackupDecision.NotDue;
        var host = await ConfigAsync(ct).ConfigureAwait(false);
        if (!host.Enabled) return BackupDecision.NotDue;
        var now = clock.UtcNow;
        var state = await store.GetVmAsync(vm.Owner, vm.Name, ct).ConfigureAwait(false);
        var open = (await store.ListAsync(vm.Owner, vm.Name, ct).ConfigureAwait(false)).Any(b => b.IsOpen && !IsStale(b, now));
        return BackupSchedule.Evaluate(host, state, open, busy, vm.Created, now);
    }

    /// <summary>
    /// The largest payload a new session may write: what the host can store above its reserve, and
    /// within the VM's quota when one is set.
    /// </summary>
    public static long MaxBytes(BackupsConfig host, long freeBytes, long storedBytes)
    {
        var max = freeBytes - host.MinFreeBytes;
        if (host.MaxBytesPerVm is long quota) max = Math.Min(max, quota - storedBytes);
        return Math.Max(0, max);
    }

    /// <summary>
    /// Removes a backup's files and row. An upload into it is cancelled; it removes its own partial file.
    /// A file that cannot be removed now (open elsewhere) is left to the hourly cleanup, which removes
    /// payloads without a row.
    /// </summary>
    public async Task DeleteLockedAsync(ConfigBackup backup, CancellationToken ct)
    {
        if (_uploads.TryGetValue(backup.Id, out var upload))
        {
            try { await upload.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
        }
        else
        {
            try { files.Delete(backup.Owner, backup.Vm, backup.Id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not remove the payload of backup {Id} yet ({Error}); cleanup retries.", backup.Id, SafeError.Describe(ex));
            }
        }

        await store.DeleteAsync(backup.Id, ct).ConfigureAwait(false);
        TryPrune(backup.Owner, backup.Vm);
    }

    /// <summary>Applies rotation to one pair (§3.6); returns how many backups it removed.</summary>
    public async Task<int> RotateLockedAsync(string owner, string vm, BackupsConfig host, BackupVmState? state, CancellationToken ct)
    {
        var backups = await store.ListAsync(owner, vm, ct).ConfigureAwait(false);
        var expired = BackupRotation.Expired(backups, BackupSchedule.EffectiveKeep(host, state), clock.UtcNow);
        foreach (var id in expired)
        {
            await DeleteLockedAsync(backups.First(b => b.Id == id), ct).ConfigureAwait(false);
        }
        if (expired.Count > 0) logger.LogInformation("Backup rotation removed {Count} backup(s) of {Vm}.", expired.Count, vm);
        return expired.Count;
    }

    /// <summary>Records a skipped or failed run: the first one after a success starts the failure streak.</summary>
    public async Task RecordFailureLockedAsync(string owner, string vm, string trigger, string reason, string? detail, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var state = await store.GetVmAsync(owner, vm, ct).ConfigureAwait(false) ?? new BackupVmState(owner, vm);
        await store.SaveVmAsync(state with
        {
            LastAttemptAt = now,
            LastFailure = new BackupFailure(now, trigger, reason, detail),
            FailingSince = state.FailingSince ?? now,
            RequestPendingAt = null,
        }, ct).ConfigureAwait(false);
    }

    private void TryPrune(string owner, string vm)
    {
        try { files.PruneDirectory(owner, vm); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
