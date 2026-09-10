using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Hosting;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Constructd.Windows.Updates;
using Microsoft.Extensions.DependencyInjection;
namespace Constructd.Tests.Updates;

public sealed class UpdateFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verified_package_stage_apply_reconnect_completes_the_persisted_job(bool autoApply)
    {
        using var fixture=new PackageTests();var(m,zip)=fixture.Package();
        var root=Path.Combine(Path.GetTempPath(),"update-flow-"+Guid.NewGuid().ToString("n"));Directory.CreateDirectory(root);
        try
        {
            using var app=new TestApp(new Dictionary<string,string?>{["Constructd:DatabasePath"]=Path.Combine(root,"db"),["Constructd:ScriptsDir"]=root},
                s=>s.AddSingleton<IUpdateStager>(sp=>sp.GetRequiredService<PackageStager>()));
            using var admin=await app.CreateUserClientAsync("admin",Role.Admin);
            var config=app.Service<IHostConfigStore>();await config.SetAsync("updates",HostAdminDefaults.Updates,"test",default);
            var source=app.Service<FakeReleaseSource>();
            var assets=new[]{("manifest.json",JsonSerializer.SerializeToUtf8Bytes(m,UpdateFiles.Json)),(m.PayloadAsset,zip)}.Select(a=>{
                var uri=new Uri("https://github.com/permissionBRICK/The-Construct/releases/download/"+m.ReleaseTag+"/"+a.Item1);source.Assets[uri]=a.Item2;return new ReleaseAsset(a.Item1,uri,a.Item2.Length);}).ToArray();
            source.Releases.Add(new(m.ReleaseTag,m.Commit,DateTimeOffset.UtcNow,assets));
            var stage=await admin.PostAsJsonAsync("/api/v1/host/updates/stage",new{operationKey="flow-stage",autoApply});Assert.Equal(HttpStatusCode.Accepted,stage.StatusCode);
            var staged=await stage.Content.ReadFromJsonAsync<JsonElement>();var id=staged.GetProperty("updateId").GetString()!;
            await Finish(app,staged.GetProperty("jobId").GetString()!);
            var stagedRow=(await app.Service<IHostUpdateStore>().GetAsync(id,default))!;
            if(!autoApply) Assert.Equal(HostUpdateState.Staged,stagedRow.State);
            Assert.Contains(stagedRow.Phases,phase=>phase.Name=="download");
            string jobId;
            if(autoApply)
            {
                Assert.True((await config.GetAsync<Constructd.Api.Jobs.UpdateAutoApply>("update-auto-apply:"+id,default))!.Enabled);
                await app.Service<UpdateRecoveryService>().ReconcileAsync(default);
                jobId=(await config.GetAsync<Constructd.Api.Jobs.UpdateJobLink>("update-job:"+id,default))!.JobId;
            }
            else
            {
                var apply=await admin.PostAsJsonAsync("/api/v1/host/updates/apply",new{updateId=id,operationKey="flow-apply"});Assert.Equal(HttpStatusCode.Accepted,apply.StatusCode);
                var applied=await apply.Content.ReadFromJsonAsync<JsonElement>();jobId=applied.GetProperty("jobId").GetString()!;
            }
            await Finish(app,jobId);
            Assert.Equal(MaintenanceState.Maintenance,app.Service<IMaintenanceGate>().State);
            var launcher=app.Service<FakeUpdaterLauncher>();Assert.Equal(1,launcher.LaunchCount);
            var h=(await launcher.ReadHandoffAsync(default))!;
            launcher.Recovery=new(id,h.Commit,h.PreviousCommit,"commit",DateTimeOffset.UtcNow,"succeeded",null,"backup",true,true,h.StagedPath,1,[]);
            await app.Service<UpdateRecoveryService>().ReconcileAsync(default);
            Assert.Equal(MaintenanceState.Open,app.Service<IMaintenanceGate>().State);
            var job=(await app.Service<IJobEngine>().GetAsync(jobId,default))!;
            Assert.Equal(JobState.Succeeded,job.State);Assert.Equal("commit",job.Phase);
            var status=await admin.GetFromJsonAsync<JsonElement>("/api/v1/host/updates/status");Assert.Equal("succeeded",status.GetProperty("current").GetProperty("state").GetString());
            if(!autoApply)
            {
                var replay=await admin.PostAsJsonAsync("/api/v1/host/updates/apply",new{updateId=id,operationKey="flow-apply"});Assert.Equal(HttpStatusCode.OK,replay.StatusCode);Assert.True((await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replayed").GetBoolean());
            }
            await app.Service<UpdateRecoveryService>().ReconcileAsync(default);
            Assert.Equal(1,launcher.LaunchCount);
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task Missing_handoff_before_replace_reopens_and_retains_interrupted_state()
    {
        using var app=new TestApp();var row=UpdateTests.Row(new string('a',32)) with{State=HostUpdateState.HandedOff};
        await app.Service<IHostUpdateStore>().TryStartAsync(row,default);app.Service<IMaintenanceGate>().Enter(MaintenanceState.Maintenance,row.Id);
        await app.Service<UpdateRecoveryService>().ReconcileAsync(default);
        Assert.Equal(MaintenanceState.Open,app.Service<IMaintenanceGate>().State);
        Assert.Equal(HostUpdateState.Interrupted,(await app.Service<IHostUpdateStore>().GetAsync(row.Id,default))!.State);
        await app.Service<UpdateRecoveryService>().ReconcileAsync(default);
        Assert.Equal(HostUpdateState.Interrupted,(await app.Service<IHostUpdateStore>().GetAsync(row.Id,default))!.State);
    }
    [Fact] public async Task Closed_missing_handoff_can_be_applied_fresh_without_a_new_stage()
    {
        using var app=new TestApp();using var admin=await app.CreateUserClientAsync("admin",Role.Admin);
        using var fixture=new PackageTests();var(m,_)=fixture.Package();var id=new string('a',32);
        var config=app.Service<IHostConfigStore>();await config.SetAsync("updates",HostAdminDefaults.Updates,"test",default);
        var staged=new StagedUpdate(id,m,"stage",[]);app.Service<FakeUpdateStager>().Staged[id]=staged;
        await config.SetAsync("update-staged:"+id,staged,"test",default);
        await app.Service<IHostUpdateStore>().TryStartAsync(UpdateTests.Row(id) with{State=HostUpdateState.HandedOff},default);
        await app.Service<UpdateRecoveryService>().ReconcileAsync(default);
        Assert.Equal(FenceDisposition.Closed,app.Service<FakeUpdaterLauncher>().Fence!.Disposition);
        var apply=await admin.PostAsJsonAsync("/api/v1/host/updates/apply",new{updateId=id});Assert.Equal(HttpStatusCode.Accepted,apply.StatusCode);
        var body=await apply.Content.ReadFromJsonAsync<JsonElement>();await Finish(app,body.GetProperty("jobId").GetString()!);
        Assert.Null(app.Service<FakeUpdaterLauncher>().Fence);Assert.Equal(1,app.Service<FakeUpdaterLauncher>().LaunchCount);
    }
    [Fact] public async Task Transient_recovery_read_error_does_not_freeze_a_normal_host()
    {
        using var app=new TestApp();app.Service<FakeUpdaterLauncher>().FailNextRead=true;
        await app.Service<UpdateRecoveryService>().ReconcileAsync(default);Assert.Equal(MaintenanceState.Open,app.Service<IMaintenanceGate>().State);
        await app.Service<UpdateRecoveryService>().ReconcileAsync(default);Assert.Equal(MaintenanceState.Open,app.Service<IMaintenanceGate>().State);
    }
    [Fact] public async Task Resume_immediately_after_abort_is_accepted()
    {
        using var app=new TestApp();using var admin=await app.CreateUserClientAsync("admin",Role.Admin);
        var config=app.Service<IHostConfigStore>();await config.SetAsync("updates",HostAdminDefaults.Updates,"test",default);
        using var fixture=new PackageTests();var(m,_)=fixture.Package();var id=new string('a',32);
        var staged=new StagedUpdate(id,m,"stage",[]);app.Service<FakeUpdateStager>().Staged[id]=staged;await config.SetAsync("update-staged:"+id,staged,"test",default);
        await app.Service<IHostUpdateStore>().TryStartAsync(UpdateTests.Row(id) with{State=HostUpdateState.Interrupted},default);
        var launcher=app.Service<FakeUpdaterLauncher>();var h=new UpdateHandoff(id,m.Commit,"stage","publish","scripts","data","constructd",app.Service<IReleaseInfo>().Installed.Commit,"https://127.0.0.1/health","","cli",new string('e',64),DateTimeOffset.UtcNow);
        await launcher.PrepareAsync(h,default);launcher.Recovery=new(id,m.Commit,h.PreviousCommit,"replace",DateTimeOffset.UtcNow,"recoveryFailed",null,"backup",true,true,"stage",1,[]);
        var resolved=await admin.PostAsJsonAsync("/api/v1/host/updates/resolve",new{updateId=id,action="abort"});Assert.Equal(HttpStatusCode.OK,resolved.StatusCode);
        var apply=await admin.PostAsJsonAsync("/api/v1/host/updates/apply",new{updateId=id});Assert.Equal(HttpStatusCode.Accepted,apply.StatusCode);
        var body=await apply.Content.ReadFromJsonAsync<JsonElement>();await Finish(app,body.GetProperty("jobId").GetString()!);Assert.Equal(1,launcher.LaunchCount);
        Assert.Equal(HostUpdateState.HandedOff,(await app.Service<IHostUpdateStore>().GetAsync(id,default))!.State);
    }
    [Fact] public async Task Cancelling_a_drain_reopens_the_gate_and_records_cancelled()
    {
        using var app=new TestApp();using var admin=await app.CreateUserClientAsync("admin",Role.Admin);
        using var fixture=new PackageTests();var(m,_)=fixture.Package();var id=new string('a',32);
        var config=app.Service<IHostConfigStore>();await config.SetAsync("updates",HostAdminDefaults.Updates,"test",default);
        var staged=new StagedUpdate(id,m,"stage",[]);app.Service<FakeUpdateStager>().Staged[id]=staged;await config.SetAsync("update-staged:"+id,staged,"test",default);
        await app.Service<IHostUpdateStore>().TryStartAsync(UpdateTests.Row(id),default);
        using var blocker=app.Service<IMaintenanceGate>().TryEnter("iso-build","blocking-job",null);
        var apply=await admin.PostAsJsonAsync("/api/v1/host/updates/apply",new{updateId=id});Assert.Equal(HttpStatusCode.Accepted,apply.StatusCode);
        var body=await apply.Content.ReadFromJsonAsync<JsonElement>();var jobId=body.GetProperty("jobId").GetString()!;
        var status=await admin.GetFromJsonAsync<JsonElement>("/api/v1/host/updates/status");
        Assert.Equal("blocking-job",status.GetProperty("current").GetProperty("blockingJobs")[0].GetString());
        var cancel=await admin.PostAsJsonAsync("/api/v1/host/updates/cancel",new{updateId=id});Assert.Equal(HttpStatusCode.OK,cancel.StatusCode);
        await Finish(app,jobId);
        Assert.Equal(JobState.Cancelled,(await app.Service<IJobEngine>().GetAsync(jobId,default))!.State);
        Assert.Equal(HostUpdateState.Cancelled,(await app.Service<IHostUpdateStore>().GetAsync(id,default))!.State);
        Assert.Equal(MaintenanceState.Open,app.Service<IMaintenanceGate>().State);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Closed_pre_replace_interruption_can_be_cancelled_then_a_different_release_staged(bool hasHandoff)
    {
        using var fixture=new PackageTests();var(m,zip)=fixture.Package();
        var root=Path.Combine(Path.GetTempPath(),"update-cancel-"+Guid.NewGuid().ToString("n"));Directory.CreateDirectory(root);
        try
        {
            using var app=new TestApp(new Dictionary<string,string?>{["Constructd:DatabasePath"]=Path.Combine(root,"db")},s=>s.AddSingleton<IUpdateStager>(sp=>sp.GetRequiredService<PackageStager>()));
            using var admin=await app.CreateUserClientAsync("admin",Role.Admin);
            await app.Service<IHostConfigStore>().SetAsync("updates",HostAdminDefaults.Updates,"test",default);
            var id=new string('d',32);var old=UpdateTests.Row(id) with{Commit=new string('e',40),State=HostUpdateState.HandedOff};
            await app.Service<IHostUpdateStore>().TryStartAsync(old,default);
            var launcher=app.Service<FakeUpdaterLauncher>();
            if(hasHandoff) await launcher.PrepareAsync(new(id,old.Commit,"stage","publish","scripts","data","constructd",app.Service<IReleaseInfo>().Installed.Commit,"https://127.0.0.1/health","","cli","test",DateTimeOffset.UtcNow),default);
            await app.Service<UpdateRecoveryService>().ReconcileAsync(default);
            Assert.Equal(HostUpdateState.Interrupted,(await app.Service<IHostUpdateStore>().GetAsync(id,default))!.State);
            var cancel=await admin.PostAsJsonAsync("/api/v1/host/updates/cancel",new{updateId=id});Assert.Equal(HttpStatusCode.OK,cancel.StatusCode);
            await app.Service<UpdateRecoveryService>().ReconcileAsync(default);
            Assert.Equal(HostUpdateState.Cancelled,(await app.Service<IHostUpdateStore>().GetAsync(id,default))!.State);
            Assert.Equal(FenceDisposition.Closed,launcher.Fence!.Disposition);
            var source=app.Service<FakeReleaseSource>();
            var assets=new[]{("manifest.json",JsonSerializer.SerializeToUtf8Bytes(m,UpdateFiles.Json)),(m.PayloadAsset,zip)}.Select(a=>{
                var uri=new Uri("https://github.com/permissionBRICK/The-Construct/releases/download/"+m.ReleaseTag+"/"+a.Item1);source.Assets[uri]=a.Item2;return new ReleaseAsset(a.Item1,uri,a.Item2.Length);}).ToArray();
            source.Releases.Add(new(m.ReleaseTag,m.Commit,DateTimeOffset.UtcNow,assets));
            var response=await admin.PostAsJsonAsync("/api/v1/host/updates/stage",new{releaseTag=m.ReleaseTag});Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
            var body=await response.Content.ReadFromJsonAsync<JsonElement>();await Finish(app,body.GetProperty("jobId").GetString()!);
            var next=(await app.Service<IHostUpdateStore>().GetAsync(body.GetProperty("updateId").GetString()!,default))!;
            Assert.Equal(HostUpdateState.Staged,next.State);Assert.NotEqual(old.Commit,next.Commit);
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task Cancel_cannot_revoke_an_interruption_after_replacement()
    {
        using var app=new TestApp();var id=new string('a',32);var row=UpdateTests.Row(id) with{State=HostUpdateState.Interrupted};
        await app.Service<IHostUpdateStore>().TryStartAsync(row,default);var launcher=app.Service<FakeUpdaterLauncher>();
        await launcher.TryWriteFenceAsync(new(id,FenceDisposition.Closed,"system",DateTimeOffset.UtcNow),default);
        launcher.Recovery=new(id,row.Commit,"old","replace",DateTimeOffset.UtcNow,null,null,"backup",true,true,"stage",0,[]);
        var error=await Assert.ThrowsAsync<Constructd.Core.Logic.UpdateException>(()=>app.Service<Constructd.Api.Jobs.HostUpdateJob>().CancelAsync(id,"admin",default));
        Assert.Equal("update-not-cancellable",error.Code);
    }
    [Fact] public async Task Deleted_handoff_after_terminal_success_does_not_manufacture_recovery_failure()
    {
        using var app=new TestApp();var id=new string('a',32);var row=UpdateTests.Row(id) with{State=HostUpdateState.Succeeded};
        await app.Service<IHostUpdateStore>().UpsertAsync(row,default);
        await app.Service<IHostConfigStore>().SetAsync("maintenance",new MaintenanceMarker(MaintenanceState.Maintenance,id,DateTimeOffset.UtcNow),"test",default);
        app.Service<FakeUpdaterLauncher>().Recovery=new(id,row.Commit,"old","commit",DateTimeOffset.UtcNow,"succeeded",null,"backup",true,true,"stage",1,[]);
        await app.Service<UpdateRecoveryService>().ReconcileAsync(default);
        Assert.Equal(MaintenanceState.Open,app.Service<IMaintenanceGate>().State);
        Assert.Equal(HostUpdateState.Succeeded,(await app.Service<IHostUpdateStore>().GetAsync(id,default))!.State);
    }
    private static async Task Finish(TestApp app,string id){await foreach(var _ in app.Service<IJobEngine>().SubscribeAsync(id,default)){}}
}
