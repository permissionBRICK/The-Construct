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
/// The host remembers the one request it has open per VM; an upload it did not ask for goes to the VM's
/// slot (docs/plans/construct-platform-design.md 10.2).
/// </summary>
public sealed class BackupCoordinator(IBackupStore store, IBackupFiles files, IHostConfigStore config, IClock clock,
    IMaintenanceGate maintenance, ConstructdOptions options, ILogger<BackupCoordinator> logger, ChangeFeed changes)
{
    /// <summary>The engine's change stream: <c>backup.added</c> and <c>backup.removed</c>.</summary>
    public ChangeFeed Changes => changes;

    private readonly InMemoryVmOperationGate _locks = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _uploads = new(StringComparer.Ordinal);

    /// <summary>How often an upload re-checks the host's free space (64 MiB; tests lower it).</summary>
    public long SpaceCheckBytes { get; set; } = 64L << 20;

    public IBackupStore Store => store;
    public IBackupFiles Files => files;

    /// <summary>The largest slot upload; the slot's budget is this for its content plus this for staging.</summary>
    public long SlotMaxBytes => options.Backups.SlotMaxBytes;

    /// <summary>The retention horizon of <see cref="BackupRotation.Protected"/>.</summary>
    public int MinHorizonDays => options.Backups.MinHorizonDays;

    public async Task<BackupsConfig> ConfigAsync(CancellationToken ct) =>
        await config.GetAsync<BackupsConfig>("backups", ct).ConfigureAwait(false) ?? HostAdminDefaults.Backups;

    public Task<IAsyncDisposable> LockAsync(string owner, string vm, CancellationToken ct) =>
        _locks.AcquireAsync(owner.ToLowerInvariant() + "\n" + vm.ToLowerInvariant(), "backup", ct);

    public static bool Belongs(ConfigBackup backup, Vm vm) =>
        Ownership.SameName(backup.Owner, vm.Owner) && Ownership.SameName(backup.Vm, vm.Name);

    public bool IsUploading(string id) => _uploads.ContainsKey(id);

    /// <summary>
    /// An open session nothing is writing that is past <see cref="BackupSchedule.StaleSession"/>, or for a
    /// slot session past <see cref="BackupSchedule.SlotSessionIdle"/>.
    /// </summary>
    public bool IsStale(ConfigBackup backup, DateTimeOffset now) =>
        backup.IsOpen && now - backup.CreatedAt >= (backup.IsSlot ? BackupSchedule.SlotSessionIdle : BackupSchedule.StaleSession)
        && !IsUploading(backup.Id);

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

    /// <summary>
    /// The heartbeat's question (§3.6 "Due"), primaries only; never while the host drains for maintenance.
    /// A due scheduled backup opens the host's request, so the VM's next create counts as scheduled; an
    /// open request is asked for again with its own trigger and keeps its time. A slot session never
    /// holds the question back.
    /// </summary>
    public async Task<BackupDecision> HeartbeatAsync(Vm vm, bool busy, CancellationToken ct)
    {
        if (vm.Kind != VmKind.Primary || maintenance.State != MaintenanceState.Open) return BackupDecision.NotDue;
        var host = await ConfigAsync(ct).ConfigureAwait(false);
        if (!host.Enabled) return BackupDecision.NotDue;
        await using var held = await LockAsync(vm.Owner, vm.Name, ct).ConfigureAwait(false);
        var now = clock.UtcNow;
        var state = await store.GetVmAsync(vm.Owner, vm.Name, ct).ConfigureAwait(false);
        var open = (await store.ListAsync(vm.Owner, vm.Name, ct).ConfigureAwait(false)).Any(b => b.IsOpen && !b.IsSlot && !IsStale(b, now));
        var decision = BackupSchedule.Evaluate(host, state, open, busy, vm.Created, now);
        if (decision.Due && !BackupSchedule.RequestPending(state, now))
            await store.SaveVmAsync((state ?? new BackupVmState(vm.Owner, vm.Name)) with
            {
                RequestPendingAt = now,
                RequestTrigger = decision.Trigger,
            }, ct).ConfigureAwait(false);
        return decision;
    }

    /// <summary>Opens the host's one request for a backup of the VM, replacing an open one; the caller holds the pair's lock.</summary>
    public async Task OpenRequestLockedAsync(string owner, string vm, string trigger, CancellationToken ct)
    {
        var state = await store.GetVmAsync(owner, vm, ct).ConfigureAwait(false) ?? new BackupVmState(owner, vm);
        await store.SaveVmAsync(state with { RequestPendingAt = clock.UtcNow, RequestTrigger = trigger }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The largest payload a new session may write: what the host can store above its reserve, and
    /// within the budget of its kind: the quota less the host-requested backups stored, or the slot's
    /// own <see cref="SlotMaxBytes"/>.
    /// </summary>
    public long MaxBytes(BackupsConfig host, bool slot, long freeBytes, long storedBytes)
    {
        ArgumentNullException.ThrowIfNull(host);
        var budget = slot ? SlotMaxBytes : BackupSchedule.Quota(host) - storedBytes;
        return Math.Max(0, Math.Min(freeBytes - host.MinFreeBytes, budget));
    }

    /// <summary>The bytes of complete backups that count against a session's budget: host-requested ones, or none for a slot.</summary>
    public static long StoredBytes(IEnumerable<ConfigBackup> backups, bool slot) =>
        slot ? 0 : backups.Where(b => b.IsComplete && !b.IsSlot).Sum(b => b.SizeBytes ?? 0);

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

        if (await store.DeleteAsync(backup.Id, ct).ConfigureAwait(false) && backup.IsComplete)
            changes.Publish("backup.removed", backup.Id, backup.Vm, backup.Owner);
        TryPrune(backup.Owner, backup.Vm);
    }

    /// <summary>Applies rotation to one pair (§3.6); returns how many backups it removed.</summary>
    public async Task<int> RotateLockedAsync(string owner, string vm, BackupsConfig host, BackupVmState? state, CancellationToken ct)
    {
        var backups = await store.ListAsync(owner, vm, ct).ConfigureAwait(false);
        var expired = BackupRotation.Expired(backups, BackupSchedule.EffectiveKeep(host, state), clock.UtcNow, MinHorizonDays);
        foreach (var id in expired)
        {
            await DeleteLockedAsync(backups.First(b => b.Id == id), ct).ConfigureAwait(false);
        }
        if (expired.Count > 0) logger.LogInformation("Backup rotation removed {Count} backup(s) of {Vm}.", expired.Count, vm);
        return expired.Count;
    }

    /// <summary>
    /// Records a skipped or failed run: the first one after a success starts the failure streak. With
    /// <paramref name="answersRequest"/> the run answered the open request: it is the attempt and consumes the
    /// request. Without it (an upload whose session already consumed its request at create), the attempt time
    /// and a request opened since stay as they are.
    /// </summary>
    public async Task RecordFailureLockedAsync(string owner, string vm, string trigger, string reason, string? detail, bool answersRequest,
        CancellationToken ct)
    {
        var now = clock.UtcNow;
        var state = await store.GetVmAsync(owner, vm, ct).ConfigureAwait(false) ?? new BackupVmState(owner, vm);
        state = state with { LastFailure = new BackupFailure(now, trigger, reason, detail), FailingSince = state.FailingSince ?? now };
        if (answersRequest) state = state with { LastAttemptAt = now, RequestPendingAt = null, RequestTrigger = null };
        await store.SaveVmAsync(state, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A PC key was enrolled on the VM. When the last run failed for lack of one
    /// (<c>no-recipients</c>), that failure and its streak are cleared and the attempt is
    /// forgotten, so the next heartbeat asks for a backup again instead of waiting for the
    /// retry delay. Returns whether anything was cleared.
    /// </summary>
    public async Task<bool> RecordEnrolmentLockedAsync(string owner, string vm, CancellationToken ct)
    {
        var state = await store.GetVmAsync(owner, vm, ct).ConfigureAwait(false);
        if (state?.LastFailure is not { Reason: NoRecipientsReason }) return false;
        await store.SaveVmAsync(state with { LastFailure = null, FailingSince = null, LastAttemptAt = null }, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>The reason a guest reports when no PC key is enrolled on it.</summary>
    public const string NoRecipientsReason = "no-recipients";

    private void TryPrune(string owner, string vm)
    {
        try { files.PruneDirectory(owner, vm); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
