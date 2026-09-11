using System.Security.Cryptography;
using System.Text.Json;
using Constructd.Api.Admin;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Windows.Updates;
namespace Constructd.Api.Jobs;

/// <summary>Serializes update acceptance; execution belongs to durable jobs and the independent task.</summary>
public sealed class HostUpdateJob(IHostUpdateStore store, IReleaseSource source, IUpdateStager stager, PackageStager checker,
    IUpdaterLauncher launcher, IHostLock hostLock, IMaintenanceGate gate, IHostConfigStore config,
    IReleaseInfo release, ConstructdOptions options, IClock clock, IJobStore jobs, IPersistedJobRunner runner,
    IJobEngine engine, IAuditLog audit, IHostUpdateAdmission admission)
{
    public DateTimeOffset LaunchDeadline { get; private set; }
    public bool LaunchInProgress { get; private set; }
    public SemaphoreSlim Acceptance { get; } = new(1,1);
    public string DataDir => Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!;
    public string Root => Path.Combine(DataDir,"updates");
    public async Task<HostUpdateRecord> PhaseAsync(HostUpdateRecord row, HostUpdateState state, string phase, string? error = null)
    {
        row = row with { State=state, Phase=phase, Error=error, Phases=[..row.Phases,new(phase,clock.UtcNow,null,error)],
            Finished=state is HostUpdateState.Checking or HostUpdateState.Staged or HostUpdateState.Draining or HostUpdateState.HandedOff or HostUpdateState.Applying or HostUpdateState.Interrupted ? null : clock.UtcNow };
        await store.UpsertAsync(row,CancellationToken.None);
        await audit.AppendAsync(new(clock.UtcNow,row.Actor,"host.update.phase",row.Id,error is null ? AuditOutcome.Success : AuditOutcome.Failure,
            $"op={row.Id}, initiator={row.Actor}, phase={phase}, state={state}"),CancellationToken.None);
        return row;
    }
    public async Task<object> StageAsync(string? tag, string actor, CancellationToken ct, OperationKeyRecord? operation=null, bool autoApply=false)
    {
        var id=Guid.NewGuid().ToString("n");
        var row=new HostUpdateRecord(id,"",tag,null,HostUpdateState.Checking,"check",[],clock.UtcNow,null,null,release.Installed.Commit,actor,[]);
        if(hostLock.IsHeldByAnotherProcess("updater.lock")) throw new UpdateException("update-in-progress");
        return await StartJobAsync(row,"stage",async (jobId,p,token) =>
        {
            try
            {
                await runner.SetPhaseAsync(jobId,"check",token);
                var settings=await checker.SettingsAsync(token);
                var latest=(await source.ListHostReleasesAsync(settings.Repository,token,tag)).OrderByDescending(r=>r.PublishedAt).FirstOrDefault(r=>tag is null || r.Tag==tag)
                    ?? throw new UpdateException("release-source-unreachable");
                row=await PhaseAsync(row with { Commit=latest.Commit, ReleaseTag=latest.Tag },HostUpdateState.Checking,"check");
                var progress=new PhaseProgress(async phase=>
                {row=await PhaseAsync(row,HostUpdateState.Checking,phase);await runner.SetPhaseAsync(jobId,phase,token);p.Report(phase);});
                StagedUpdate staged;
                try {staged=await stager.StageAsync(id,latest,progress,token);}
                finally {await progress.CompleteAsync();}
                await config.SetAsync("update-staged:"+id,staged,actor,token);
                // Persist consent before publishing Staged. Recovery can continue the
                // install even after the requesting client disconnects or a restart.
                if(autoApply) await config.SetAsync("update-auto-apply:"+id,new UpdateAutoApply(true),actor,token);
                row=await PhaseAsync(row with { PackageVersion=staged.Manifest.PackageVersion },HostUpdateState.Staged,"verify");
                await runner.SetPhaseAsync(jobId,"verify",token);
                return new JobOutcome(row);
            }
            catch (Exception ex)
            { row=await PhaseAsync(row, ex is OperationCanceledException ? HostUpdateState.Cancelled : HostUpdateState.StageFailed,row.Phase ?? "verify",SafeCode(ex)); if(ex is OperationCanceledException or UpdateException) throw; throw new UpdateException(SafeCode(ex)); }
        },ct,operation);
    }
    private sealed class PhaseProgress(Func<string,Task> update) : IProgress<string>
    {
        private readonly object _gate=new();private Task _pending=Task.CompletedTask;
        public void Report(string value)
        {
            if(value is not ("check" or "download" or "verify"))return;
            lock(_gate) _pending=RunAsync(_pending,value);
        }
        private async Task RunAsync(Task prior,string value){await prior;await update(value);}
        public Task CompleteAsync(){lock(_gate)return _pending;}
    }
    public async Task<object> ApplyAsync(string id, string actor, CancellationToken ct, OperationKeyRecord? operation=null)
    {
        if (!options.Fake && string.IsNullOrWhiteSpace(options.CertThumbprint)) throw new UpdateException("update-health-pin-required");
        var row=await store.GetAsync(id,ct) ?? throw new UpdateException("update-not-staged");
        var fence=await launcher.ReadFenceAsync(ct);
        var authorizedRollback=row.State==HostUpdateState.ResolvedByAdmin && fence?.UpdateId==id && fence.Disposition==FenceDisposition.RollbackAuthorized;
        if (!authorizedRollback && row.State is not (HostUpdateState.Staged or HostUpdateState.Interrupted)) throw new UpdateException("update-not-staged");
        var resume=row.State==HostUpdateState.Interrupted || authorizedRollback;
        if (!resume && gate.State!=MaintenanceState.Open) throw new UpdateException("maintenance");
        if (hostLock.IsHeldByAnotherProcess("updater.lock")) throw new UpdateException("updater-running");
        var staged=await config.GetAsync<StagedUpdate>("update-staged:"+id,ct) ?? throw new UpdateException("update-not-staged");
        if (!await stager.VerifyStagedAsync(staged,ct)) throw new UpdateException("coverage-failed");
        var handoff=resume ? await launcher.ReadHandoffAsync(ct) : null;
        if(resume && (handoff?.UpdateId!=id || fence?.UpdateId==id && fence.Disposition==FenceDisposition.Closed))
        {
            var record=await launcher.ReadRecoveryRecordAsync(ct);
            if(record?.UpdateId==id && record.ReplaceStarted) throw new UpdateException("update-not-interrupted");
            // A fenced pre-replacement interruption is a fresh apply, with a new backup.
            resume=false;handoff=null;
        }
        if(!resume && gate.State!=MaintenanceState.Open) throw new UpdateException("maintenance");
        row=row with {State=HostUpdateState.Draining,Phase="drain"};
        return await StartJobAsync(row,"apply", async(jobId,p,token)=>
        {
            var handedOff=false;
            try
            {
                await runner.SetPhaseAsync(jobId,"drain",token);
                var settings=await checker.SettingsAsync(token);
                if (!resume)
                {
                    await config.SetAsync("maintenance",new MaintenanceMarker(MaintenanceState.Draining,id,clock.UtcNow),actor,token);
                    var timeout=TimeSpan.FromMinutes(settings.DrainTimeoutMinutes);
                    var elapsed=System.Diagnostics.Stopwatch.StartNew();
                    var drained=await gate.DrainAsync(timeout,token);
                    if(!drained.Drained) { row=row with { BlockingJobs=drained.Blocking.Select(b=>b.OperationId).ToArray() }; throw new UpdateException("drain-timeout"); }
                    await using var admin=await hostLock.TryAcquireAsync("admin.lock",timeout-elapsed.Elapsed > TimeSpan.Zero ? timeout-elapsed.Elapsed : TimeSpan.Zero,token)
                        ?? throw new UpdateException("drain-timeout");
                    // Freeze all mutations and finish previously admitted inline work before the DB backup.
                    gate.Enter(MaintenanceState.Maintenance,id);
                    while(gate.LiveHandles>0)
                    { if(elapsed.Elapsed>=timeout) throw new UpdateException("drain-timeout"); await Task.Delay(25,token); }
                    handoff=new(id,row.Commit,staged.StagedPath,AppContext.BaseDirectory,options.ScriptsDir,DataDir,"constructd",release.Installed.Commit,
                        "https://127.0.0.1:"+(new Uri(options.ListenUrl ?? "https://0.0.0.0:7462").Port)+"/api/v1/health",options.CertThumbprint ?? "",
                        Path.Combine(AppContext.BaseDirectory,"Constructd.Api.exe"),Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),clock.UtcNow,release.SchemaVersion,settings.HealthTimeoutSeconds);
                    LaunchInProgress=true;
                    row=await PhaseAsync(row,HostUpdateState.HandedOff,"handoff");
                    await config.SetAsync("maintenance",new MaintenanceMarker(MaintenanceState.Maintenance,id,clock.UtcNow),actor,token);
                    await using var updater=await hostLock.TryAcquireAsync("updater.lock",TimeSpan.Zero,token) ?? throw new UpdateException("updater-running");
                    await launcher.PrepareAsync(handoff,token);
                }
                if(resume)
                {
                    LaunchInProgress=true;
                    row=await PhaseAsync(row,HostUpdateState.HandedOff,"handoff");
                }
                // The admin lock above has been released. A durable marker protects the handoff gap.
                gate.Enter(MaintenanceState.Maintenance,id);
                await runner.SetPhaseAsync(jobId,"handoff",token);
                handedOff=true;
                try { if(resume) await launcher.ResumeAsync(handoff!,token); else await launcher.LaunchAsync(handoff!,token); }
                finally { LaunchDeadline=DateTimeOffset.UtcNow.AddSeconds(30);LaunchInProgress=false; }
                return new JobOutcome(row);
            }
            catch(Exception ex)
            {
                LaunchInProgress=false;
                // A task may have started even when schtasks failed to acknowledge /Run. Fence it before reopening.
                var fenced=!resume && (!handedOff || await launcher.TryWriteFenceAsync(new(id,FenceDisposition.Closed,actor,clock.UtcNow),CancellationToken.None));
                row=await PhaseAsync(row,fenced ? ex is OperationCanceledException ? HostUpdateState.Cancelled : HostUpdateState.ApplyFailed : HostUpdateState.Interrupted,"handoff",SafeCode(ex));
                if(fenced) await ReopenAsync(actor);
                else
                {
                    gate.Enter(MaintenanceState.Maintenance,id);
                    await config.SetAsync("maintenance",new MaintenanceMarker(MaintenanceState.Maintenance,id,clock.UtcNow),actor,CancellationToken.None);
                }
                if(fenced && ex is OperationCanceledException) throw;
                throw new UpdateException(SafeCode(ex));
            }
        },ct,operation);
    }
    private async Task<object> StartJobAsync(HostUpdateRecord row,string action,Func<string,IProgress<string>,CancellationToken,Task<JobOutcome>> work,CancellationToken ct,OperationKeyRecord? operation=null)
    {
        var job=new Job(Guid.NewGuid().ToString("n"),"host-update",null,row.Actor,JobState.Queued,[],null,null,clock.UtcNow,null,Initiator:row.Actor,OperationKey:operation?.Key,Phase:row.Phase);
        var answer=new {jobId=job.Id,updateId=row.Id};
        if(operation is not null) operation=operation with {JobId=job.Id,State=OperationKeyState.Completed,ResponseJson=JsonSerializer.Serialize(answer,UpdateFiles.Json)};
        if(!await admission.TryAcceptAsync(row,job,operation,action=="stage",ct)) throw new UpdateException("update-in-progress");
        try
        {
            await config.SetAsync("update-job:"+row.Id,new UpdateJobLink(job.Id,action),row.Actor,ct);
            await runner.StartPersistedAsync(job,new EmptyHandle(),(p,t)=>work(job.Id,p,t),ct);
            return answer;
        }
        catch { await jobs.UpsertAsync(job with {State=JobState.Failed,Error="enqueue-failed",Finished=clock.UtcNow},CancellationToken.None); await PhaseAsync(row,HostUpdateState.ApplyFailed,"enqueue","enqueue-failed"); throw; }
    }
    public async Task<object> CancelAsync(string id,string actor,CancellationToken ct)
    {
        var row=await store.GetAsync(id,ct) ?? throw new UpdateException("update-not-cancellable");
        if(row.State==HostUpdateState.Interrupted)
        {
            await using var held=await hostLock.TryAcquireAsync("updater.lock",TimeSpan.Zero,ct) ?? throw new UpdateException("updater-running");
            var fence=await launcher.ReadFenceAsync(ct);
            var record=await launcher.ReadRecoveryRecordAsync(ct);
            if(fence?.UpdateId!=id || fence.Disposition!=FenceDisposition.Closed || record?.UpdateId==id && record.ReplaceStarted)
                throw new UpdateException("update-not-cancellable");
            await stager.RemoveStagedAsync(id,ct);
            row=await PhaseAsync(row,HostUpdateState.Cancelled,"cancel");
            // Keep the closed fence: any delayed task must remain revoked even after cancellation.
            return new {state=row.State};
        }
        if(row.State is not (HostUpdateState.Checking or HostUpdateState.Staged or HostUpdateState.Draining)) throw new UpdateException("update-not-cancellable");
        if(row.State==HostUpdateState.Staged)
        { await stager.RemoveStagedAsync(id,ct); row=await PhaseAsync(row,HostUpdateState.Cancelled,"cancel"); }
        else if(await config.GetAsync<UpdateJobLink>("update-job:"+id,ct) is {} link) await engine.CancelAsync(link.JobId,ct);
        return new {state=row.State};
    }
    public async Task ReopenAsync(string actor)
    { await config.SetAsync("maintenance",new MaintenanceMarker(MaintenanceState.Open,null,clock.UtcNow),actor,CancellationToken.None);gate.Reopen(); }
    public static string SafeCode(Exception ex)=>ex is UpdateException u ? u.Code : ex is OperationCanceledException ? "cancelled" : "update-failed";
    private sealed class EmptyHandle:IDisposable {public void Dispose() {}}
}
public sealed record UpdateJobLink(string JobId,string Action);
public sealed record UpdateAutoApply(bool Enabled);
