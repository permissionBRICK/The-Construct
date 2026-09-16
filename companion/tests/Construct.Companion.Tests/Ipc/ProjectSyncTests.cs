using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.State;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Dispatch;
using Microsoft.Extensions.DependencyInjection;

namespace Construct.Companion.Tests.Ipc;

public sealed class ProjectSyncTests
{
    private const string Root = "/fake/local/The-Construct/config";
    private static JsonObject Profile(string name) => new() { ["name"] = name, ["repos"] = new JsonArray(new JsonObject { ["url"] = "https://example.test/" + name + ".git" }) };
    private static void NoGit(HttpTests.Harness h) => h.Get<FakeProcessRunner, IProcessRunner>().Handler = _ => new ProcessResult(-1);

    [Fact]
    public async Task AutomaticDiscoveryCreatesAndEnablesProfilesWithoutGitAndThrottlesScans()
    {
        var clock = new FakeClock();
        await using var h = await HttpTests.Harness.Start(s => s.AddSingleton<IClock>(clock), runtimeJobs:false);
        NoGit(h);
        var instances = h.App.Services.GetRequiredService<CompanionInstances>(); var entry = instances.Get("agent-vm");
        entry.Store.SaveSelectedProjects(new JsonArray());
        instances.Host.WriteProjectProfile(Root,"disabled",Profile("disabled"));
        var ssh = (FakeSshTransport)entry.Ssh;
        ssh.ScriptHandler = (_, _) => Task.FromResult(new ProcessResult(0,"api\thttps://example.test/api.git\tmain\ndisabled\thttps://example.test/disabled.git\tmain\nlocal-only\t\tmain\nEND\n"));
        var dispatcher = h.App.Services.GetRequiredService<MessageDispatcher>();
        await dispatcher.SyncProjectsAsync(entry,CancellationToken.None,automatic:true);
        Assert.Equal("api",Assert.Single(entry.Store.ReadSelectedProjects())!.GetValue<string>());
        Assert.NotNull(instances.Host.ReadProjectProfile(Root,"api"));
        Assert.Null(instances.Host.ReadProjectProfile(Root,"local-only"));
        Assert.Single(ssh.Scripts,s=>s==ProjectImport.ScanScript());
        await dispatcher.SyncProjectsAsync(entry,CancellationToken.None,automatic:true);
        Assert.Single(ssh.Scripts,s=>s==ProjectImport.ScanScript());
        clock.Advance(TimeSpan.FromMinutes(5));
        await dispatcher.SyncProjectsAsync(entry,CancellationToken.None,automatic:true);
        Assert.Equal(2,ssh.Scripts.Count(s=>s==ProjectImport.ScanScript()));
        Assert.Equal("api",Assert.Single(entry.Store.ReadSelectedProjects())!.GetValue<string>());
        Assert.Empty(h.Get<FakePrompts,IPrompts>().Shown);
    }

    [Fact]
    public async Task ManualSyncDiscoversImmediatelyWithoutOpeningRemoteImportPicker()
    {
        await using var h = await HttpTests.Harness.Start(runtimeJobs:false); NoGit(h);
        var instances=h.App.Services.GetRequiredService<CompanionInstances>(); var entry=instances.Get("agent-vm");
        entry.Store.SaveSelectedProjects(new JsonArray());
        var ssh=(FakeSshTransport)entry.Ssh; ssh.ScriptHandler=(_,_)=>Task.FromResult(new ProcessResult(0,"api\thttps://example.test/api.git\tmain\nEND\n"));
        var dispatcher=h.App.Services.GetRequiredService<MessageDispatcher>();
        await dispatcher.SyncProjectsAsync(entry,CancellationToken.None,automatic:true);
        ssh.ScriptHandler=(_,_)=>Task.FromResult(new ProcessResult(0,"api\thttps://example.test/api.git\tmain\nworker\thttps://example.test/worker.git\tmain\nEND\n"));
        await dispatcher.DispatchAsync(entry.Name,new() { ["type"]="command",["id"]="syncConfigNow" },CancellationToken.None);
        Assert.Equal(new[]{"api","worker"},entry.Store.ReadSelectedProjects().Select(StateJson.String));
        Assert.NotNull(instances.Host.ReadProjectProfile(Root,"worker"));
        Assert.Empty(h.Get<FakePrompts,IPrompts>().Shown);
        var rows=h.App.Services.GetRequiredService<StateAggregation>().State(entry.Name)["state"]!["projects"]!.AsArray();
        Assert.All(rows,p=>Assert.True(p!["selected"]!.GetValue<bool>()));
    }

    [Theory]
    [InlineData(255,"")]
    [InlineData(0,"api\thttps://example.test/api.git\tmain\n")]
    public async Task FailedOrIncompleteScanDoesNotCreateOrSelectProfiles(int code,string output)
    {
        await using var h=await HttpTests.Harness.Start(runtimeJobs:false); NoGit(h);
        var instances=h.App.Services.GetRequiredService<CompanionInstances>(); var entry=instances.Get("agent-vm");
        entry.Store.SaveSelectedProjects(new JsonArray("existing"));
        ((FakeSshTransport)entry.Ssh).ScriptHandler=(_,_)=>Task.FromResult(new ProcessResult(code,output));
        await h.App.Services.GetRequiredService<MessageDispatcher>().SyncProjectsAsync(entry,CancellationToken.None,automatic:true);
        Assert.Null(instances.Host.ReadProjectProfile(Root,"api"));
        Assert.Equal("existing",Assert.Single(entry.Store.ReadSelectedProjects())!.GetValue<string>());
    }

    [Fact]
    public async Task NewProfileIsSelectedButEditingADeselectedProfileKeepsItDeselected()
    {
        await using var h=await HttpTests.Harness.Start(runtimeJobs:false); NoGit(h);
        var instances=h.App.Services.GetRequiredService<CompanionInstances>(); var entry=instances.Get("agent-vm");
        entry.Store.SaveSelectedProjects(new JsonArray());
        var dispatcher=h.App.Services.GetRequiredService<MessageDispatcher>();
        JsonObject Save()=>new() { ["type"]="saveProject",["name"]="api",["profile"]=Profile("api") };
        await dispatcher.DispatchAsync(entry.Name,Save(),CancellationToken.None);
        Assert.Equal("api",Assert.Single(entry.Store.ReadSelectedProjects())!.GetValue<string>());
        entry.Store.SaveSelectedProjects(new JsonArray());
        await dispatcher.DispatchAsync(entry.Name,Save(),CancellationToken.None);
        Assert.Empty(entry.Store.ReadSelectedProjects());
    }

    [Fact]
    public async Task HostProfilesAppearBeforeAnySelectionWasSaved()
    {
        await using var h=await HttpTests.Harness.Start(runtimeJobs:false);
        var instances=h.App.Services.GetRequiredService<CompanionInstances>(); var entry=instances.Get("agent-vm");
        Assert.False(entry.Store.HasPersistedSelection());
        instances.Host.WriteProjectProfile(Root,"api",Profile("api"));
        var state=h.App.Services.GetRequiredService<StateAggregation>().State(entry.Name,new() { ["projects"]=new JsonArray(new JsonObject { ["name"]="live",["selected"]=true }) });
        var rows=state["state"]!["projects"]!.AsArray();
        var project=Assert.Single(rows,p=>StateJson.String(p!["name"])=="api");
        Assert.Equal("api",project!["name"]!.GetValue<string>()); Assert.False(project["selected"]!.GetValue<bool>());
        Assert.True(Assert.Single(rows,p=>StateJson.String(p!["name"])=="live")!["selected"]!.GetValue<bool>());
    }

    [Fact]
    public async Task DiscoveryUsesEachInstancesTransportThrottleAndSelection()
    {
        await using var h=await HttpTests.Harness.Start(s=>s.AddCompanionFakes(true),runtimeJobs:false); NoGit(h);
        var instances=h.App.Services.GetRequiredService<CompanionInstances>();
        var local=instances.Get("agent-vm"); var remote=instances.Get("remote-vm");
        local.Store.SaveSelectedProjects(new JsonArray()); remote.Store.SaveSelectedProjects(new JsonArray());
        ((FakeSshTransport)local.Ssh).ScriptHandler=(_,_)=>Task.FromResult(new ProcessResult(0,"local\thttps://example.test/local.git\tmain\nEND\n"));
        ((FakeSshTransport)remote.Ssh).ScriptHandler=(_,_)=>Task.FromResult(new ProcessResult(0,"remote\thttps://example.test/remote.git\tmain\nEND\n"));
        var dispatcher=h.App.Services.GetRequiredService<MessageDispatcher>();
        await dispatcher.SyncProjectsAsync(local,CancellationToken.None,automatic:true);
        await dispatcher.SelectAsync(remote.Name,CancellationToken.None);
        await dispatcher.SyncProjectsAsync(remote,CancellationToken.None,automatic:true);
        Assert.Equal("local",Assert.Single(local.Store.ReadSelectedProjects())!.GetValue<string>());
        Assert.Equal("remote",Assert.Single(remote.Store.ReadSelectedProjects())!.GetValue<string>());
        Assert.Single(((FakeSshTransport)local.Ssh).Scripts,s=>s==ProjectImport.ScanScript());
        Assert.Single(((FakeSshTransport)remote.Ssh).Scripts,s=>s==ProjectImport.ScanScript());
    }

    [Fact]
    public async Task DiscoveryDoesNotRecreateIntentionallyDeletedProfiles()
    {
        await using var h=await HttpTests.Harness.Start(runtimeJobs:false);
        var instances=h.App.Services.GetRequiredService<CompanionInstances>(); var entry=instances.Get("agent-vm");
        entry.Store.SaveSelectedProjects(new JsonArray());
        h.Get<FakeProcessRunner,IProcessRunner>().Handler=invocation=>invocation.Arguments.Contains("--diff-filter=D")
            ? new ProcessResult(0,"projects/deleted.json\n") : new ProcessResult(-1);
        ((FakeSshTransport)entry.Ssh).ScriptHandler=(_,_)=>Task.FromResult(new ProcessResult(0,"deleted\thttps://example.test/deleted.git\tmain\napi\thttps://example.test/api.git\tmain\nEND\n"));
        await h.App.Services.GetRequiredService<MessageDispatcher>().SyncProjectsAsync(entry,CancellationToken.None,automatic:true);
        Assert.Null(instances.Host.ReadProjectProfile(Root,"deleted"));
        Assert.Equal("api",Assert.Single(entry.Store.ReadSelectedProjects())!.GetValue<string>());
    }

    [Fact]
    public async Task FirstDiscoveryKeepsLiveSelectionWhenNoSelectionWasSaved()
    {
        await using var h=await HttpTests.Harness.Start(runtimeJobs:false); NoGit(h);
        var instances=h.App.Services.GetRequiredService<CompanionInstances>(); var entry=instances.Get("agent-vm");
        instances.Host.WriteProjectProfile(Root,"live",Profile("live"));
        ((FakeSshTransport)entry.Ssh).ScriptHandler=(script,_)=>Task.FromResult(new ProcessResult(0,script==ProjectImport.ScanScript()
            ? "api\thttps://example.test/api.git\tmain\nEND\n" : "PROJECTS\tlive\n"));
        await h.App.Services.GetRequiredService<MessageDispatcher>().SyncProjectsAsync(entry,CancellationToken.None,automatic:true);
        Assert.Equal(new[]{"live","api"},entry.Store.ReadSelectedProjects().Select(StateJson.String));
        var project=Assert.Single(h.App.Services.GetRequiredService<StateAggregation>().State(entry.Name)["state"]!["projects"]!.AsArray(),p=>StateJson.String(p!["name"])=="api");
        Assert.Equal("api",project!["name"]!.GetValue<string>()); Assert.True(project["selected"]!.GetValue<bool>());
    }
}
