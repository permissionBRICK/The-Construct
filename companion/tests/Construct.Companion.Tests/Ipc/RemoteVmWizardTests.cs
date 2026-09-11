using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Lifecycle;
using Construct.Companion.Core.State;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Dispatch;
using Microsoft.Extensions.DependencyInjection;
namespace Construct.Companion.Tests.Ipc;

public sealed class RemoteVmWizardTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PanelAndHostAdminUseTheSameWizard(bool hostAdmin)
    {
        var api=new RoutingRemoteApi();var fallback=api.Handle;
        api.Handle=r=>r.Url.AbsolutePath=="/api/v1/vm-defaults"?new(200,JsonSerializer.SerializeToElement(new {recommendedCpus=4,maximumCpus=8})):fallback(r);
        await using var h=await HttpTests.Harness.Start(s=>s.AddSingleton<IRemoteApi>(api),runtimeJobs:false);
        h.Files.WriteFileAtomic("/fake/scripts/Auto-Install.ps1","param($Backend,$ServiceUrl,$InstanceName,$VmCpuCount)"u8);
        var prompts=h.Get<FakePrompts,IPrompts>();foreach(var input in new[]{"work-vm","4","8","50"}) prompts.Inputs.Enqueue(input);
        var hosts=h.App.Services.GetRequiredService<HostAdministration>();await hosts.AddAsync(new("https://host.example:7462",null,new string('a',64)),CancellationToken.None);
        if(hostAdmin)
        {
            await hosts.DispatchAsync("host.example_7462",new(){["type"]="hostadmin.ready"},CancellationToken.None);
            await hosts.DispatchAsync("host.example_7462",new(){["type"]="hostadmin.action",["action"]="createFirstVm"},CancellationToken.None);
        }
        else await h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync("agent-vm",new(){["type"]="command",["id"]="createFirstVm"},CancellationToken.None);
        Assert.Single(h.Get<FakeLauncher,ILauncher>().Detached);Assert.Empty(h.Get<FakeLauncher,ILauncher>().Elevated);
        Assert.DoesNotContain(api.Requests,r=>r.Method=="POST");
    }
    [Theory]
    [InlineData(false,false,"negotiate")] [InlineData(true,false,"negotiate")]
    [InlineData(false,true,"token")] [InlineData(true,true,"token")]
    public async Task LaunchesTheInstallersCompleteRemoteFlowWithoutElevation(bool cpuSupport,bool projectSupport,string auth)
    {
        await using var h=await HttpTests.Harness.Start(runtimeJobs:false);
        var code="param($Backend,$ServiceUrl,$InstanceName"+(cpuSupport?",$VmCpuCount":"")+(projectSupport?",$Projects":"")+")";
        h.Files.WriteFileAtomic("/fake/scripts/Auto-Install.ps1",System.Text.Encoding.UTF8.GetBytes(code));
        var prompts=h.Get<FakePrompts,IPrompts>();foreach(var input in new[]{"work-vm","4","8","50"}) prompts.Inputs.Enqueue(input);
        if(projectSupport) prompts.Picks.Enqueue([]);
        var api=h.Get<FakeRemoteApi,IRemoteApi>();api.Handler=_=>new(200,JsonSerializer.SerializeToElement(new {recommendedCpus=4,maximumCpus=8}));
        await h.App.Services.GetRequiredService<ITokenStore>().WriteAsync("localhost_7462",new Secret("test-auth"));
        await h.App.Services.GetRequiredService<RemoteVmWizard>().RunAsync(new("localhost_7462","http://localhost:7462",auth,false,null),CancellationToken.None);
        var pairs=RemoteVmLaunch.ArgSpec("http://localhost:7462",auth,"work-vm",4,8,50,cpuSupport,projectSupport?[]:null);
        var expected=PowerShellLaunch.BuildHostLaunch("/fake/scripts/Auto-Install.ps1",RemoteVmLaunch.Arguments(pairs),argSpec:pairs).Invocation("/fake/scripts");
        var launcher=h.Get<FakeLauncher,ILauncher>();Assert.Equal(expected.Arguments,Assert.Single(launcher.Detached).Arguments);Assert.Empty(launcher.Elevated);
        Assert.Equal("/api/v1/vm-defaults",Assert.Single(api.Requests).Url.AbsolutePath);
        Assert.All(api.Requests,r=>Assert.Equal("GET",r.Method));
        Assert.False(h.App.Services.GetRequiredService<Host.Composition.CompanionInstances>().Registry.ByName.ContainsKey("work-vm"));
    }
    [Theory]
    [InlineData("param($Backend,$ServiceUrl)")] [InlineData("param($InstanceName)")]
    public async Task OldInstallerCannotFallThroughToLocalCreation(string code)
    {
        await using var h=await HttpTests.Harness.Start(runtimeJobs:false);
        h.Files.WriteFileAtomic("/fake/scripts/Auto-Install.ps1",System.Text.Encoding.UTF8.GetBytes(code));
        var prompts=h.Get<FakePrompts,IPrompts>();foreach(var input in new[]{"work-vm","4","8","50"}) prompts.Inputs.Enqueue(input);
        h.Get<FakeRemoteApi,IRemoteApi>().Handler=_=>new(200,JsonSerializer.SerializeToElement(new {recommendedCpus=4,maximumCpus=8}));
        await h.App.Services.GetRequiredService<RemoteVmWizard>().RunAsync(new("localhost_7462","http://localhost:7462","negotiate",false,null),CancellationToken.None);
        Assert.Empty(h.Get<FakeLauncher,ILauncher>().Detached);Assert.Empty(prompts.Shown.OfType<PickPrompt>());
    }
}
