using Constructd.Api.Admin;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Windows.Updates;
namespace Constructd.Api.Hosting;

public sealed class UpdateRecoveryService(HostUpdateJob work,IHostUpdateStore store,IUpdaterLauncher launcher,IHostLock hostLock,
    IMaintenanceGate gate,IHostConfigStore config,IReleaseInfo release,IUpdateStager stager,ConstructdOptions options,IClock clock,IServiceProvider services,IJobStore jobs,ILogger<UpdateRecoveryService> logger) : BackgroundService
{
    private bool _initialized;
    private bool _wasFrozen;
    public override async Task StartAsync(CancellationToken ct) { await ReconcileAsync(ct); await base.StartAsync(ct); }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));
        try { while(await timer.WaitForNextTickAsync(ct)) await ReconcileAsync(ct); }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
    }
    public async Task ReconcileAsync(CancellationToken ct)
    {
        if(!await work.Acceptance.WaitAsync(0,ct)) return;
        try
        {
            var marker=await config.GetAsync<MaintenanceMarker>("maintenance",ct);
            if(!_initialized && marker?.State==MaintenanceState.Maintenance)
            {gate.Enter(MaintenanceState.Maintenance,marker.UpdateId);_wasFrozen=true;}
            var pending=await store.GetActiveAsync(ct);
            if(!_initialized && pending?.State is HostUpdateState.HandedOff or HostUpdateState.Applying or HostUpdateState.Interrupted or HostUpdateState.RecoveryFailed)
            {gate.Enter(MaintenanceState.Maintenance,pending.Id);_wasFrozen=true;}
            var h=await launcher.ReadHandoffAsync(ct);
            if(!_initialized)
            {
                _initialized=true;
                if(pending?.State is HostUpdateState.Checking or HostUpdateState.Draining && h?.UpdateId!=pending.Id)
                {
                    await work.PhaseAsync(pending,pending.State==HostUpdateState.Checking ? HostUpdateState.StageFailed : HostUpdateState.ApplyFailed,"recovery","interrupted");
                    await OpenAsync(ct);pending=null;
                }
            }
            if(pending is not null && h is not null && pending.Id!=h.UpdateId) return;
            if(h is null)
            {
                pending ??= marker?.UpdateId is {} id ? await store.GetAsync(id,ct) : null;
                if(pending is null)
                {
                    // A transient read failure on an ordinary host cannot manufacture maintenance.
                    if(gate.State!=MaintenanceState.Open && !hostLock.IsHeldByAnotherProcess("updater.lock")) await OpenAsync(ct);
                    return;
                }
                if(pending.State is HostUpdateState.Succeeded or HostUpdateState.RolledBack or HostUpdateState.RolledBackWithDatabase or HostUpdateState.Cancelled)
                { await OpenAsync(ct); return; }
                if(pending.State is HostUpdateState.Checking or HostUpdateState.Staged or HostUpdateState.Draining) return;
                if(hostLock.IsHeldByAnotherProcess("updater.lock")) {await FreezeAsync(pending.Id,ct);return;}
                var recovery=await launcher.ReadRecoveryRecordAsync(ct);
                if(recovery?.UpdateId==pending.Id && recovery.ReplaceStarted)
                {
                    await FreezeAsync(pending.Id,ct);
                    if(pending.State!=HostUpdateState.RecoveryFailed) await work.PhaseAsync(pending,HostUpdateState.RecoveryFailed,"recovery","handoff-missing-after-replace");
                    return;
                }
                // Crash after persisting handedOff but before handoff.json: nothing was replaced.
                // Fence a delayed task before reopening, retain Interrupted for an explicit fresh apply.
                if(await launcher.TryWriteFenceAsync(new(pending.Id,FenceDisposition.Closed,"system",clock.UtcNow),ct))
                {
                    if(pending.State is HostUpdateState.HandedOff or HostUpdateState.Applying)
                        await work.PhaseAsync(pending,HostUpdateState.Interrupted,"recovery","handoff-missing");
                    await OpenAsync(ct);
                }
                return;
            }
            var row=await store.GetAsync(h.UpdateId,ct);
            if(row is null) {await FreezeAsync(h.UpdateId,ct);return;}
            var record=await launcher.ReadRecoveryRecordAsync(ct);if(record?.UpdateId!=h.UpdateId)record=null;
            if(row.State==HostUpdateState.Draining || (row.State==HostUpdateState.HandedOff && record is null && (work.LaunchInProgress || DateTimeOffset.UtcNow<work.LaunchDeadline)))return;
            var fence=await launcher.ReadFenceAsync(ct);if(fence?.UpdateId!=h.UpdateId)fence=null;
            var own=release.Installed.Commit;
            HostUpdateState? state=null;bool open=false;
            if(record?.Outcome=="succeeded") {state=HostUpdateState.Succeeded;open=true;}
            else if(record?.Outcome is "rolledBack" or "rolledBackWithDatabase")
            {
                open=own==h.PreviousCommit && await VerifyInstalledAsync(h,false,ct);
                state=open ? Enum.Parse<HostUpdateState>(record.Outcome,true) : HostUpdateState.RecoveryFailed;
            }
            else if(fence?.Disposition==FenceDisposition.CommitOnly && own==h.Commit) {state=HostUpdateState.ResolvedByAdmin;open=true;}
            else if(fence?.Disposition==FenceDisposition.Closed && own==h.PreviousCommit)
            {state=record?.Outcome=="applyFailed" ? HostUpdateState.ApplyFailed : row.State;open=true;}
            else if(fence?.Disposition==FenceDisposition.RollbackAuthorized)
                state=hostLock.IsHeldByAnotherProcess("updater.lock") ? HostUpdateState.Applying : row.State;
            else if(record?.Outcome=="recoveryFailed")state=HostUpdateState.RecoveryFailed;
            else if(hostLock.IsHeldByAnotherProcess("updater.lock"))state=HostUpdateState.Applying;
            else if(own==h.PreviousCommit && record?.ReplaceStarted!=true)
            {
                if(await launcher.TryWriteFenceAsync(new(h.UpdateId,FenceDisposition.Closed,"system",clock.UtcNow),ct))
                {state=HostUpdateState.Interrupted;open=true;}
            }
            else state=HostUpdateState.Interrupted;
            if(state is {} next && (row.State!=next || (record is not null && row.Phase!=record.Phase)))
                row=await work.PhaseAsync(row,next,record?.Phase ?? "recovery",record?.Error);
            if(open)
            {
                await OpenAsync(ct);
                if(row.State is HostUpdateState.Succeeded or HostUpdateState.RolledBack or HostUpdateState.RolledBackWithDatabase or HostUpdateState.ApplyFailed)
                {
                    var link=await config.GetAsync<UpdateJobLink>("update-job:"+row.Id,ct);
                    var job=link is null ? null : await jobs.GetAsync(link.JobId,ct);
                    if(job is not null && (job.State is JobState.Queued or JobState.Running || job.Error=="interrupted by a service restart" || job.Phase!=row.Phase))
                        await jobs.UpsertAsync(job with {State=row.State==HostUpdateState.Succeeded ? JobState.Succeeded : JobState.Failed,
                            Finished=clock.UtcNow,Result=row,Phase=row.Phase,Error=row.State==HostUpdateState.Succeeded ? null : row.Error ?? "update-rolled-back"},ct);
                }
            }
            else await FreezeAsync(h.UpdateId,ct);
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            // Preserve the previous gate state and retry. A normal host with no recovery work must
            // not be locked out because of a transient filesystem/store read failure.
            if(!_initialized) {gate.Enter(MaintenanceState.Maintenance,null);_wasFrozen=true;}
            logger.LogWarning("Update recovery check failed; retrying with the current maintenance state.");
        }
        finally {work.Acceptance.Release();}
    }
    private async Task FreezeAsync(string id,CancellationToken ct)
    {
        _wasFrozen=true;gate.Enter(MaintenanceState.Maintenance,id);
        var marker=await config.GetAsync<MaintenanceMarker>("maintenance",ct);
        if(marker?.State!=MaintenanceState.Maintenance || marker.UpdateId!=id)
            await config.SetAsync("maintenance",new MaintenanceMarker(MaintenanceState.Maintenance,id,clock.UtcNow),"system",ct);
    }
    private async Task OpenAsync(CancellationToken ct)
    {
        var marker=await config.GetAsync<MaintenanceMarker>("maintenance",ct);
        if(_wasFrozen || gate.State!=MaintenanceState.Open || marker is not null && marker.State!=MaintenanceState.Open)
        {
            // Initialize persisted forwards and finish interrupted jobs before accepting writes.
            _wasFrozen=true;
            await Constructd.Api.Composition.Bootstrap.RunAsync(services,ct,resumeAfterUpdate:true);
            await work.ReopenAsync("system");
            _wasFrozen=false;
        }
    }
    public async Task<object> ResolveAsync(string id,string action,string actor,CancellationToken ct)
    {
        var row=await store.GetAsync(id,ct) ?? throw new UpdateException("update-not-resolvable");
        if(row.State is not (HostUpdateState.Interrupted or HostUpdateState.RecoveryFailed)) throw new UpdateException("update-not-resolvable");
        await using var held=await hostLock.TryAcquireAsync("updater.lock",TimeSpan.Zero,ct) ?? throw new UpdateException("updater-running");
        var h=await launcher.ReadHandoffAsync(ct);
        if(h?.UpdateId!=id) throw new UpdateException("update-not-commitable");
        var record=await launcher.ReadRecoveryRecordAsync(ct);
        FenceDisposition disposition;
        switch(action)
        {
            case "commit":
                if(release.Installed.Commit!=h.Commit) throw new UpdateException("wrong-binary");
                var staged=await config.GetAsync<StagedUpdate>("update-staged:"+id,ct);
                if(staged is null) throw new UpdateException("installation-mixed");
                var health=options.Fake ? new DatabaseHealth("ok",release.SchemaVersion) : await AdminDbCheck.CheckAsync(options.DatabasePath,ct);
                if(health.Status!="ok" || health.SchemaVersion<staged.Manifest.Database.SchemaVersion) throw new UpdateException("health-failed");
                if(staged is null || !await stager.VerifyStagedAsync(staged,ct) || !await VerifyInstalledAsync(h,true,ct)) throw new UpdateException("installation-mixed");
                disposition=FenceDisposition.CommitOnly;break;
            case "abort":
                if(record?.UpdateId!=id || !record.BackupComplete) throw new UpdateException("backup-incomplete");
                disposition=FenceDisposition.RollbackAuthorized;break;
            case "close":
                if(release.Installed.Commit!=h.PreviousCommit || !await VerifyInstalledAsync(h,false,ct,true)) throw new UpdateException("installation-mixed");
                disposition=FenceDisposition.Closed;break;
            default:throw new UpdateException("update-not-commitable");
        }
        // The lock covers preconditions AND fence persistence. The launcher's helper takes its own lock,
        // so production writes here while holding ours, while the fake stores a logical fence.
        var fence=new UpdateFence(id,disposition,actor,clock.UtcNow);
        if(options.Fake) {if(!await launcher.TryWriteFenceAsync(fence,ct)) throw new UpdateException("updater-running");}
        else await UpdateFiles.WriteAsync(Path.Combine(work.Root,"fence.json"),fence,ct);
        await work.PhaseAsync(row,HostUpdateState.ResolvedByAdmin,"resolve:"+action);
        if(disposition==FenceDisposition.RollbackAuthorized) gate.Enter(MaintenanceState.Maintenance,id); else await OpenAsync(ct);
        return new {state=HostUpdateState.ResolvedByAdmin,action};
    }
    public static async Task<bool> VerifyInstalledAsync(UpdateHandoff h,bool current,CancellationToken ct,bool requireLedger=false)
    {
        try
        {
            var backup=Path.Combine(h.DataDir,"updates","backup-"+h.UpdateId);
            IReadOnlyList<UpdateFile>? files;
            var old=await UpdateFiles.ReadAsync<InstallRecord>(Path.Combine(backup,"previous-install.json"),ct);
            if(current) files=(await UpdateFiles.ReadAsync<VerifiedFiles>(Path.Combine(h.StagedPath,"verified.json"),ct))?.Files;
            else files=old?.Files ?? (requireLedger ? null : await UpdateFiles.ReadAsync<UpdateFile[]>(Path.Combine(backup,"files.json"),ct));
            if(files is null || files.Count==0) return false;
            string Target(string p)=>Path.Combine(p.StartsWith("service/",StringComparison.Ordinal) ? h.PublishDir : h.ScriptsDir,p[8..]);
            foreach(var file in files.Where(f=>f.Path.StartsWith("service/",StringComparison.Ordinal)||f.Path.StartsWith("scripts/",StringComparison.Ordinal)))
            {
                if(!ZipEntryRules.IsSafe(file.Path)||ZipEntryRules.IsPreserved(file.Path)) return false;
                var target=Target(file.Path);UpdateFiles.NoLinks(target);
                if(!File.Exists(target)||UpdateFiles.Sha256(target)!=file.Sha256) return false;
            }
            if(current && old is not null)
                foreach(var file in old.Files.Where(f=>!files.Any(n=>n.Path.Equals(f.Path,StringComparison.OrdinalIgnoreCase))))
                    if(!ZipEntryRules.IsPreserved(file.Path) && (!ZipEntryRules.IsSafe(file.Path)||File.Exists(Target(file.Path)))) return false;
            return true;
        }
        catch(Exception ex) when(ex is IOException or System.Text.Json.JsonException or UpdateException) {return false;}
    }
}
