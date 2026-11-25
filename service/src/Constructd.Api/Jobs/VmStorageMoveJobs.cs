using Constructd.Api.Endpoints;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;

namespace Constructd.Api.Jobs;

public sealed class StorageMoveRequestException(int status, string code, string message) : Exception(message), IConstructdError
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>
/// Relocates a VM's virtual hard disk to another directory on the host (<see cref="JobKinds.MoveStorage"/>),
/// keeping the capacity ledger's storage reservations in sync so moved VMs are never double-counted or flagged
/// as missing artifacts. Supports both single-VM moves and sequential host-wide moves after changing the host's
/// default storage path.
/// </summary>
public sealed class VmStorageMoveJobs(
    IVmRepository vms,
    IHypervisorDriver driver,
    IHostConfigStore config,
    ConstructdOptions options,
    IVmOperationGate gates,
    IOperationRegistry operations,
    IOperationKeyStore keys,
    IMaintenanceGate maintenance,
    ICapacityLedger capacity,
    IHypervisorInventory inventory,
    IJobStore jobs,
    IPersistedJobRunner runner,
    IAuditLog audit,
    IClock clock,
    ChangeFeed changes,
    IHostApplicationLifetime lifetime,
    ILogger<VmStorageMoveJobs> logger,
    IVmStorageDriver? storageDriver = null)
{
    public static readonly string[] Steps = ["move", "finish"];

    public bool Supported => storageDriver is not null;

    public async Task<string> ResolveDestinationAsync(string? requestedPath, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            if (StoragePathRules.Problem(requestedPath, allowEmpty: false) is { } error)
                throw new StorageMoveRequestException(400, "validation", error);
            return StoragePathRules.NormalizeDirectory(requestedPath);
        }

        var stored = (await config.GetAsync<StorageConfig>("storage", ct))?.VmStorageRoot;
        return StoragePathRules.ResolveEffectivePrimaryDirectory(stored, options);
    }

    public async Task<Job> SubmitAsync(Vm vm, string? destinationPath, string actor, CancellationToken ct)
    {
        if (storageDriver is null)
            throw new StorageMoveRequestException(409, "unsupported-on-host", "Moving VM disk storage is not supported on this host.");

        var destDir = await ResolveDestinationAsync(destinationPath, ct);

        await using var gate = await PrimaryOperationGate.AcquireAsync(gates, vm.Name, "move-check:" + Guid.NewGuid().ToString("n"), ct);
        if (gate is null)
            throw new StorageMoveRequestException(409, "vm-busy", "Another operation is already running on this VM.");

        var fresh = await vms.GetAsync(vm.Name, ct) ?? throw new StorageMoveRequestException(404, "not-found", "Unknown VM.");
        if (fresh.Deleting)
            throw new StorageMoveRequestException(409, "vm-deleting", "VM is being deleted.");

        if (fresh.CurrentJobId is { } liveId && await jobs.GetAsync(liveId, ct) is { State: JobState.Queued or JobState.Running })
            throw new StorageMoveRequestException(409, "vm-busy", "Another operation is already running on this VM.");

        if ((await keys.ListInFlightAsync(fresh.Name, ct)).Any())
            throw new StorageMoveRequestException(409, "operation-in-progress", "Another operation is already in flight for this VM.");

        var state = await driver.GetStateAsync(fresh.Name, ct);
        if (state == VmState.Saved)
            throw new StorageMoveRequestException(409, "vm-saved", "Turn off or start the VM before moving its disk (saved VMs cannot have their storage moved).");

        var id = Guid.NewGuid().ToString("n");
        var maint = maintenance.TryEnter("mutation:" + JobKinds.MoveStorage, id, fresh.Name)
            ?? throw new StorageMoveRequestException(503, "maintenance", "Host is in maintenance mode.");
        IDisposable? handle = new CombinedHandle(maint, operations.Register(id, JobKinds.MoveStorage, fresh.Name));

        var queued = new Job(id, JobKinds.MoveStorage, fresh.Name, fresh.Owner, JobState.Queued, [], null, null, clock.UtcNow, null, actor, null, Steps[0]);
        try
        {
            await runner.StartPersistedAsync(queued, handle,
                (progress, token) => RunAsync(id, fresh, destDir, actor, progress, token), CancellationToken.None);
            handle = null;
            return queued;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    public async Task<(IReadOnlyList<string> Vms, string DestinationPath)> StartAllAsync(string? destinationPath, string actor, CancellationToken ct)
    {
        if (storageDriver is null)
            throw new StorageMoveRequestException(409, "unsupported-on-host", "Moving VM disk storage is not supported on this host.");

        var destDir = await ResolveDestinationAsync(destinationPath, ct);
        var all = (await vms.ListAsync(null, ct))
            .Where(v => !v.Deleting)
            .OrderBy(v => v.Kind == VmKind.Primary ? 0 : 1)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var snap = await capacity.SnapshotAsync(false, ct);
        var candidates = new List<Vm>();
        foreach (var vm in all)
        {
            var currentPath = KnownDiskPath(vm, snap);
            var currentDir = StoragePathRules.DirectoryOf(currentPath);
            if (currentDir is not null && StoragePathRules.SameDirectory(currentDir, destDir)) continue;
            candidates.Add(vm);
        }

        var names = candidates.Select(v => v.Name).ToArray();
        if (names.Length == 0) return (names, destDir);

        var stopping = lifetime.ApplicationStopping;
        var batch = candidates.Select(v => (v.Name, v.Owner, v.Created)).ToArray();
        _ = Task.Run(async () =>
        {
            foreach (var (name, owner, created) in batch)
            {
                if (stopping.IsCancellationRequested) break;
                try
                {
                    if (await vms.GetAsync(name, stopping) is not { Deleting: false } current ||
                        !Ownership.SameName(current.Owner, owner) || current.Created != created) continue;

                    var job = await SubmitAsync(current, destDir, actor, stopping);
                    while (await jobs.GetAsync(job.Id, stopping) is { State: JobState.Queued or JobState.Running })
                        await Task.Delay(TimeSpan.FromMilliseconds(250), stopping);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogInformation("Batch storage move for {Vm} skipped: {Reason}", name,
                        ex is StorageMoveRequestException sm ? sm.Code : SafeError.Describe(ex));
                }
            }
        });

        return (names, destDir);
    }

    private async Task<JobOutcome> RunAsync(string jobId, Vm vm, string destDir, string actor, IProgress<string> progress, CancellationToken ct)
    {
        JobProgressTracker.Current?.Expect(Steps);
        IReadOnlyList<string> pendingIds = [];
        try
        {
            await using var gate = await gates.AcquireAsync(vm.Name, jobId, ct);
            var current = await vms.GetAsync(vm.Name, ct) ?? throw new LifecycleException("vm-deleting");
            if (current.Deleting) throw new LifecycleException("vm-deleting");

            JobProgressTracker.Current?.Begin("move", "Moving disk");
            var snap = await capacity.SnapshotAsync(false, ct);
            var currentPath = KnownDiskPath(current, snap);
            var currentVolume = currentPath is null ? null : StoragePathRules.VolumeRoot(currentPath);
            var destVolume = StoragePathRules.VolumeRoot(destDir);
            var fileName = currentPath is null ? current.Name + ".vhdx" : Path.GetFileName(currentPath.Replace('\\', '/'));
            var plannedDest = destDir.EndsWith('\\') ? destDir + fileName : destDir + "\\" + fileName;

            if (currentVolume is null || !string.Equals(currentVolume, destVolume, StringComparison.OrdinalIgnoreCase))
            {
                var diskBytes = KnownDiskBytes(current, snap);
                if (diskBytes > 0)
                {
                    var decision = await capacity.TryReserveAsync(
                        new ReservationRequest(current.Owner, current.Name, jobId,
                            [new ReservationLine(ReservationResource.Storage, diskBytes, "disk:" + plannedDest, destVolume)],
                            TimeSpan.FromHours(2)),
                        ct);
                    if (!decision.Allowed)
                        throw new StorageMoveRequestException(409, "capacity-exhausted",
                            $"Not enough free disk space on {destVolume} to move {current.Name}.");
                    pendingIds = decision.ReservationIds;
                }
            }

            var result = await storageDriver!.MoveStorageAsync(current.Name, destDir, progress, ct);

            JobProgressTracker.Current?.Begin("finish", "Updating storage");
            if (result.Moved || pendingIds.Count > 0)
            {
                await capacity.RelocateDiskAsync(current.Name, result.OldDiskPath, result.NewDiskPath, result.NewVolume, CancellationToken.None);
                if (currentPath is not null && !string.Equals(currentPath, result.OldDiskPath, StringComparison.OrdinalIgnoreCase))
                    await capacity.RelocateDiskAsync(current.Name, currentPath, result.NewDiskPath, result.NewVolume, CancellationToken.None);
                pendingIds = [];
            }

            changes.Vm("vm.changed", current);
            await audit.AppendAsync(new AuditEntry(clock.UtcNow, actor, "vm.storage.move.completed", current.Name, AuditOutcome.Success,
                result.Moved ? $"path={result.NewDiskPath}" : "already-in-destination"), CancellationToken.None);

            return new JobOutcome(new VmMoveStorageResult(current.Name, result.OldDiskPath, result.NewDiskPath, result.Moved));
        }
        catch (Exception ex)
        {
            var safe = SafeError.Describe(ex);
            progress.Report($"move failed: {safe}");
            if (pendingIds.Count > 0)
            {
                try
                {
                    var rows = (await capacity.SnapshotAsync(false, CancellationToken.None)).Reservations
                        .Where(r => pendingIds.Contains(r.Id)).ToArray();
                    var evidence = await inventory.ReadAsync(rows, CancellationToken.None);
                    var absent = rows.Where(r => evidence.Artifacts?.Any(a => r.Artifact == a.Artifact && a.Presence == ArtifactPresence.Absent) != false)
                        .Select(r => r.Id).ToArray();
                    if (absent.Length > 0)
                        await capacity.ReleaseAsync(absent, VmState.Absent, "move-storage-failed", CancellationToken.None);
                }
                catch { /* Keep reservation if evidence cannot be read. */ }
            }
            try
            {
                await audit.AppendAsync(new AuditEntry(clock.UtcNow, actor, "vm.storage.move.failed", vm.Name, AuditOutcome.Failure, safe), CancellationToken.None);
            }
            catch { }
            throw;
        }
    }

    private static string? KnownDiskPath(Vm vm, HostCapacitySnapshot snap)
    {
        var measured = snap.MeasuredVms?.FirstOrDefault(v => CapacityMath.Matches(vm, v));
        var disk = measured?.Disks.FirstOrDefault(d => Ownership.SameName(d.Path.Replace('\\', '/').Split('/').Last(), vm.Name + ".vhdx"))
            ?? measured?.Disks.FirstOrDefault();
        if (disk is not null) return disk.Path;
        return snap.Reservations.FirstOrDefault(r => r.Resource == ReservationResource.Storage &&
            Ownership.SameName(r.VmName, vm.Name) &&
            r.Artifact?.StartsWith("disk:", StringComparison.OrdinalIgnoreCase) == true)?.Artifact?[5..];
    }

    private static long KnownDiskBytes(Vm vm, HostCapacitySnapshot snap)
    {
        var measured = snap.MeasuredVms?.FirstOrDefault(v => CapacityMath.Matches(vm, v));
        var disk = measured?.Disks.FirstOrDefault(d => Ownership.SameName(d.Path.Replace('\\', '/').Split('/').Last(), vm.Name + ".vhdx"))
            ?? measured?.Disks.FirstOrDefault();
        if (disk is { MaxBytes: > 0 }) return disk.MaxBytes;
        var reserved = snap.Reservations.Where(r => r.Resource == ReservationResource.Storage && !ReservationRules.SavedState(r) &&
            Ownership.SameName(r.VmName, vm.Name)).Select(r => r.Amount).DefaultIfEmpty(0L).Max();
        return reserved > 0 ? reserved : (long)vm.DiskGb << 30;
    }

    private sealed class CombinedHandle(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose() { try { second.Dispose(); } finally { first.Dispose(); } }
    }
}
