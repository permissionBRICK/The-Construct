using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Admin;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Configuration;
using Constructd.Core.Logic;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Sqlite;
using Constructd.Sqlite.Migrations;
using Constructd.Tests.Support;
using Constructd.Windows.Updates;
namespace Constructd.Tests.Updates;

public class UpdateTests
{
    [Theory]
    [InlineData("../evil")][InlineData("/evil")][InlineData("C:/evil")][InlineData("scripts/../evil")]
    [InlineData("service/A.dll:stream")][InlineData("service/CON.txt")][InlineData("service/foo.")]
    [InlineData("service/foo ")][InlineData("service/a\\b")][InlineData("service//x")]
    public void Windows_path_aliases_are_refused_on_every_platform(string path)=>Assert.False(ZipEntryRules.IsSafe(path));
    [Fact] public void Normal_package_paths_are_safe()=>Assert.True(ZipEntryRules.IsPayloadFile("scripts/service/host/Update-ConstructHost.ps1"));
    [Fact] public async Task Drain_is_atomic_and_ignores_ordinary_mutations()
    {
        var gate=new InMemoryMaintenanceGate(); using var mutation=gate.TryEnter("mutation:heartbeat","heartbeat",null);
        var active=gate.TryEnter("create-vm","create","vm")!;
        var draining=gate.DrainAsync(TimeSpan.FromSeconds(5),default);
        Assert.Equal(MaintenanceState.Draining,gate.State);Assert.Null(gate.TryEnter("media-acquire","new",null));
        Assert.False(draining.IsCompleted);active.Dispose();Assert.True((await draining).Drained);
        gate.Enter(MaintenanceState.Maintenance,"u");Assert.Null(gate.TryEnter("mutation:heartbeat","later",null));
    }
    [Fact] public async Task Drain_timeout_lists_real_blockers()
    {
        var gate=new InMemoryMaintenanceGate();using var handle=gate.TryEnter("iso-build","iso",null);
        var result=await gate.DrainAsync(TimeSpan.Zero,default);
        Assert.False(result.Drained);Assert.Equal("iso",Assert.Single(result.Blocking).OperationId);
        gate.Reopen();using var after=gate.TryEnter("create-vm","create",null);Assert.NotNull(after);
    }
    [Fact] public async Task Job_engine_holds_admission_until_terminal_and_rejects_before_persistence()
    {
        var gate=new InMemoryMaintenanceGate();var store=new InMemoryJobStore();
        using var engine=new InProcessJobEngine(new MutableClock(),store,maintenance:gate);
        var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job=await engine.SubmitAsync("create-vm",null,"owner",async(_,_)=>{await finish.Task;return new(null);},default);
        var draining=gate.DrainAsync(TimeSpan.FromSeconds(5),default);
        await Assert.ThrowsAsync<UpdateException>(()=>engine.SubmitAsync("create-vm",null,"owner",(_,_)=>Task.FromResult(new JobOutcome(null)),default));
        Assert.False(draining.IsCompleted);finish.SetResult();Assert.True((await draining).Drained);
        Assert.Equal(JobState.Succeeded,(await engine.GetAsync(job.Id,default))!.State);
    }
    [Fact] public async Task Ordinary_http_admission_cannot_bypass_a_conflicting_job_drain()
    {
        var gate=new InMemoryMaintenanceGate();using var admission=new MaintenanceAdmission(gate.TryEnter("mutation:http","http",null)!,"mutation:http");
        MaintenanceAdmission.Current.Value=admission;
        try
        {
            Assert.True((await gate.DrainAsync(TimeSpan.Zero,default)).Drained);
            using var engine=new InProcessJobEngine(new MutableClock(),new InMemoryJobStore(),maintenance:gate);
            await Assert.ThrowsAsync<UpdateException>(()=>engine.SubmitAsync("iso-build",null,"admin",(_,_)=>Task.FromResult(new JobOutcome(null)),default));
        }
        finally {MaintenanceAdmission.Current.Value=null;}
    }
    [Fact] public async Task Update_rows_are_durable_and_only_one_can_be_active()
    {
        var dir=Path.Combine(Path.GetTempPath(),"updates-test-"+Guid.NewGuid().ToString("n"));Directory.CreateDirectory(dir);
        try
        {
            var db=new SqliteDatabase(Path.Combine(dir,"constructd.db"));db.EnsureCreated();var store=new SqliteHostUpdateStore(db);
            var row=Row("one");Assert.True(await store.TryStartAsync(row,default));
            var attempts=await Task.WhenAll(Enumerable.Range(0,8).Select(i=>new SqliteHostUpdateStore(db).TryStartAsync(Row("other"+i),default)));
            Assert.All(attempts,Assert.False);Assert.Equal(row.Id,(await new SqliteHostUpdateStore(db).GetActiveAsync(default))!.Id);
            await store.UpsertAsync(row with{State=HostUpdateState.Succeeded,Finished=DateTimeOffset.UtcNow},default);
            Assert.True(await store.TryStartAsync(Row("two"),default));
            Assert.Equal(2,(await store.ListAsync(10,default)).Count);
            var check=await AdminDbCheck.CheckAsync(Path.Combine(dir,"constructd.db"),default);
            Assert.Equal("ok",check.Status);Assert.Equal(SqliteMigrations.SchemaVersion,check.SchemaVersion);
        }
        finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
    }
    [Fact] public async Task Legacy_database_check_reports_schema_zero_without_migrating()
    {
        var path=Path.Combine(Path.GetTempPath(),"legacy-db-check-"+Guid.NewGuid().ToString("n")+".db");
        try
        {
            await using(var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await db.OpenAsync();await using var command=db.CreateCommand();
                command.CommandText="CREATE TABLE users (name TEXT PRIMARY KEY); INSERT INTO users VALUES ('existing-owner')";
                await command.ExecuteNonQueryAsync();
            }
            var before=await File.ReadAllBytesAsync(path);
            var result=await AdminDbCheck.CheckAsync(path,default);
            Assert.Equal(new DatabaseHealth("ok",0),result);
            Assert.Equal(before,await File.ReadAllBytesAsync(path));
        }
        finally {File.Delete(path);}
    }
    [Fact] public async Task Update_api_is_admin_only_and_check_needs_no_key()
    {
        using var app=new TestApp();using var user=await app.CreateUserClientAsync("user");using var admin=await app.CreateUserClientAsync("admin",Role.Admin);
        foreach(var action in new[]{"check","stage","apply","cancel","resolve"})
        {
            Assert.Equal(HttpStatusCode.Forbidden,(await user.PostAsJsonAsync("/api/v1/host/updates/"+action,new{})).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK,(await admin.PostAsJsonAsync("/api/v1/host/updates/check",new{})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await user.GetAsync("/api/v1/host/updates/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await admin.GetAsync("/api/v1/host/updates/status")).StatusCode);
    }
    [Fact] public async Task Maintenance_freezes_mutations_but_health_and_status_remain_readable()
    {
        using var app=new TestApp();using var admin=await app.CreateUserClientAsync("admin",Role.Admin);
        app.Service<IMaintenanceGate>().Enter(MaintenanceState.Maintenance,"update");
        var refused=await admin.PostAsJsonAsync("/api/v1/users",new{name="other"});
        Assert.Equal(HttpStatusCode.ServiceUnavailable,refused.StatusCode);Assert.NotNull(refused.Headers.RetryAfter);
        Assert.Contains("maintenance",await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK,(await admin.GetAsync("/api/v1/host/updates/status")).StatusCode);
        var health=await app.CreateAnonymousClient().GetFromJsonAsync<JsonElement>("/api/v1/health");
        Assert.Equal("maintenance",health.GetProperty("status").GetString());Assert.False(health.TryGetProperty("commit",out _));
    }
    [Fact] public async Task Failed_stage_survives_reconnect_and_is_audited()
    {
        using var app=new TestApp();using var admin=await app.CreateUserClientAsync("admin",Role.Admin);
        await app.Service<IHostConfigStore>().SetAsync("updates",HostAdminDefaults.Updates,"test",default);
        var response=await admin.PostAsJsonAsync("/api/v1/host/updates/stage",new{operationKey="stage-test-1"});
        Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);var body=await response.Content.ReadFromJsonAsync<JsonElement>();
        var id=body.GetProperty("updateId").GetString()!;
        var engine=app.Service<IJobEngine>();await foreach(var _ in engine.SubscribeAsync(body.GetProperty("jobId").GetString()!,default)){}
        var failed=(await app.Service<IHostUpdateStore>().GetAsync(id,default))!;
        Assert.Equal(HostUpdateState.StageFailed,failed.State);
        Assert.Equal("check",failed.Phase);
        Assert.Equal("check",failed.Phases.Last().Name);
        var replay=await admin.PostAsJsonAsync("/api/v1/host/updates/stage",new{operationKey="stage-test-1"});
        Assert.Equal(HttpStatusCode.OK,replay.StatusCode);Assert.Contains(id,await replay.Content.ReadAsStringAsync());
        Assert.Contains("stageFailed",await (await admin.GetAsync("/api/v1/host/updates/status")).Content.ReadAsStringAsync());
    }
    public static HostUpdateRecord Row(string id)=>new(id,new string('a',40),"host-"+new string('a',40),"version",HostUpdateState.Staged,"verify",[],DateTimeOffset.UtcNow,null,null,new string('b',40),"admin",[]);
}
