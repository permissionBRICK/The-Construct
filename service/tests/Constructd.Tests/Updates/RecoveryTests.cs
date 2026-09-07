using System.Net;
using Constructd.Api.Auth;
using Constructd.Api.Hosting;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Sqlite;
using Constructd.Tests.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
namespace Constructd.Tests.Updates;

public sealed class RecoveryTests
{
    private static UpdateHandoff Handoff(string previous)=>new(new string('a',32),new string('b',40),"stage","publish","scripts","data","constructd",previous,"https://127.0.0.1:7462/api/v1/health",new string('c',40),"cli",new string('e',64),DateTimeOffset.UtcNow);
    [Fact] public async Task Dead_updater_before_replace_is_fenced_before_writes_reopen()
    {
        using var app=new TestApp();var release=app.Service<IReleaseInfo>();var h=Handoff(release.Installed.Commit);
        await app.Service<IHostUpdateStore>().TryStartAsync(UpdateTests.Row(h.UpdateId) with {Commit=h.Commit,State=HostUpdateState.HandedOff},default);
        await app.Service<IUpdaterLauncher>().LaunchAsync(h,default);
        app.Service<IMaintenanceGate>().Enter(MaintenanceState.Maintenance,h.UpdateId);
        await app.Service<UpdateRecoveryService>().ReconcileAsync(default);
        Assert.Equal(FenceDisposition.Closed,app.Service<FakeUpdaterLauncher>().Fence!.Disposition);
        Assert.Equal(MaintenanceState.Open,app.Service<IMaintenanceGate>().State);
    }
    [Fact] public async Task Mixed_old_binary_never_reopens_and_resolution_refuses_wrong_binary()
    {
        using var app=new TestApp();var h=Handoff(app.Service<IReleaseInfo>().Installed.Commit);
        await app.Service<IHostUpdateStore>().TryStartAsync(UpdateTests.Row(h.UpdateId) with{Commit=h.Commit,State=HostUpdateState.HandedOff},default);
        var launcher=app.Service<FakeUpdaterLauncher>();await launcher.LaunchAsync(h,default);
        launcher.Recovery=new(h.UpdateId,h.Commit,h.PreviousCommit,"replace",DateTimeOffset.UtcNow,null,null,"backup",true,true,"stage",0,[]);
        await app.Service<UpdateRecoveryService>().ReconcileAsync(default);
        Assert.Equal(MaintenanceState.Maintenance,app.Service<IMaintenanceGate>().State);
        Assert.Equal(HostUpdateState.Interrupted,(await app.Service<IHostUpdateStore>().GetAsync(h.UpdateId,default))!.State);
        var ex=await Assert.ThrowsAsync<Constructd.Core.Logic.UpdateException>(()=>app.Service<UpdateRecoveryService>().ResolveAsync(h.UpdateId,"commit","admin",default));
        Assert.Equal("wrong-binary",ex.Code);
    }
    [Fact] public async Task Terminal_outcome_wins_over_stale_rollback_fence()
    {
        using var app=new TestApp();var h=Handoff(app.Service<IReleaseInfo>().Installed.Commit);
        await app.Service<IHostUpdateStore>().TryStartAsync(UpdateTests.Row(h.UpdateId) with{State=HostUpdateState.HandedOff},default);
        var launcher=app.Service<FakeUpdaterLauncher>();await launcher.LaunchAsync(h,default);
        await launcher.TryWriteFenceAsync(new(h.UpdateId,FenceDisposition.RollbackAuthorized,"admin",DateTimeOffset.UtcNow),default);
        launcher.Recovery=new(h.UpdateId,h.Commit,h.PreviousCommit,"commit",DateTimeOffset.UtcNow,"succeeded",null,"backup",true,true,"stage",1,[]);
        await app.Service<UpdateRecoveryService>().ReconcileAsync(default);
        Assert.Equal(MaintenanceState.Open,app.Service<IMaintenanceGate>().State);
        Assert.Equal(HostUpdateState.Succeeded,(await app.Service<IHostUpdateStore>().GetAsync(h.UpdateId,default))!.State);
    }
    [Fact] public async Task Health_handoff_authentication_is_loopback_health_only_and_has_no_role()
    {
        using var app=new TestApp();var h=Handoff(app.Service<IReleaseInfo>().Installed.Commit);
        await app.Service<IUpdaterLauncher>().LaunchAsync(h,default);app.Service<IMaintenanceGate>().Enter(MaintenanceState.Maintenance,h.UpdateId);
        async Task<AuthenticateResult> Authenticate(string path,IPAddress ip)
        {
            using var scope=app.Services.CreateScope();var http=new DefaultHttpContext {RequestServices=scope.ServiceProvider};
            http.Request.Path=path;http.Request.Method="GET";http.Connection.RemoteIpAddress=ip;http.Request.Headers.Authorization="UpdateHandoff "+h.HealthToken;
            return await scope.ServiceProvider.GetRequiredService<IAuthenticationService>().AuthenticateAsync(http,UpdateHandoffAuthenticationHandler.SchemeName);
        }
        var success=await Authenticate("/api/v1/health",IPAddress.Loopback);Assert.True(success.Succeeded);Assert.False(success.Principal!.IsInRole("Admin"));
        Assert.False((await Authenticate("/api/v1/host/config",IPAddress.Loopback)).Succeeded);
        Assert.False((await Authenticate("/api/v1/health",IPAddress.Parse("192.0.2.1"))).Succeeded);
        app.Service<IMaintenanceGate>().Reopen();Assert.False((await Authenticate("/api/v1/health",IPAddress.Loopback)).Succeeded);
    }
    [Fact] public async Task Forward_reconcile_failure_does_not_keep_a_recovered_host_in_maintenance()
    {
        var forwards=new RecoveryForwards();
        using var app=new TestApp(null,s=>s.AddSingleton<IPortForwardManager>(forwards));
        var gate=app.Service<IMaintenanceGate>();
        forwards.Fail=true;
        gate.Enter(MaintenanceState.Maintenance,null);
        await app.Service<UpdateRecoveryService>().ReconcileAsync(default);
        Assert.Equal(MaintenanceState.Open,app.Service<IMaintenanceGate>().State);
    }
    private sealed class RecoveryForwards : IPortForwardManager
    {
        public bool Fail {get;set;}
        public Task<int> ReconcileAsync(CancellationToken ct)=>Fail ? Task.FromException<int>(new IOException("test failure")) : Task.FromResult(0);
        public Task<int> AllocateSshForwardAsync(string vm,CancellationToken ct)=>Task.FromResult(2200);
        public Task<bool> ReleaseSshForwardAsync(string vm,CancellationToken ct)=>Task.FromResult(false);
        public Task<AddForwardResult> TryAddForwardAsync(string vm,int port,ForwardTarget target,string label,int max,CancellationToken ct)=>throw new NotSupportedException();
        public Task<bool> RemoveForwardAsync(string vm,string id,CancellationToken ct)=>Task.FromResult(false);
        public Task<int> RemoveAllForwardsAsync(string vm,CancellationToken ct)=>Task.FromResult(0);
        public Task<IReadOnlyList<PortForward>> ListAsync(string? vm,CancellationToken ct)=>Task.FromResult<IReadOnlyList<PortForward>>([]);
        public Task<int> CountActiveConnectionsAsync(string vm,CancellationToken ct)=>Task.FromResult(0);
    }
    [Fact] public async Task Update_acceptance_rolls_back_state_and_job_when_replay_key_conflicts()
    {
        var root=Path.Combine(Path.GetTempPath(),"update-atomic-"+Guid.NewGuid().ToString("n"));Directory.CreateDirectory(root);
        try
        {
            var db=new SqliteDatabase(Path.Combine(root,"constructd.db"));db.EnsureCreated();var store=new SqliteHostUpdateStore(db);var keys=new SqliteOperationKeyStore(db);var jobs=new SqliteJobStore(db);
            var row=UpdateTests.Row("update");var job=new Job("job","host-update",null,"admin",JobState.Queued,[],null,null,DateTimeOffset.UtcNow,null);
            var key=new OperationKeyRecord("admin","host-update:stage","test-key","fingerprint","host","job",OperationKeyState.Completed,null,null,"{}",DateTimeOffset.UtcNow);
            await keys.TryInsertAsync(key,default);
            Assert.False(await store.TryAcceptAsync(row,job,key,true,default));Assert.Null(await store.GetAsync(row.Id,default));Assert.Null(await jobs.GetAsync(job.Id,default));
            Assert.True(await store.TryAcceptAsync(row,job,key with{Key="other-key"},true,default));
            Assert.NotNull(await jobs.GetAsync(job.Id,default));Assert.NotNull(await keys.GetAsync("admin","host-update:stage","other-key",default));
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    }
}
