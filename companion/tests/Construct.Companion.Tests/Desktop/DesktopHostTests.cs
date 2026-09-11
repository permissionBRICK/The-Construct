using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Construct.Companion.Core;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Core.State;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Desktop;
using Construct.Companion.Host.Runtime;
namespace Construct.Companion.Tests.Desktop;

public sealed class DesktopHostTests
{
    [Fact]
    public async Task ActivationHandoffUsesAuthenticatedHttpAndCleansPrivateEndpoint()
    {
        var files=new FakeFileSystem(); var ui=new FakeUiActivation();
        await using (var server=await DesktopActivationServer.StartAsync(files,"/state/ui-endpoint.json",ui,"fixture"))
        {
            using var http=new HttpClient(new HttpClientHandler { UseProxy=false,AllowAutoRedirect=false }) { Timeout=TimeSpan.FromSeconds(5) };
            var client=new ActivationClient(files,http,new SystemClock());
            await client.SendAsync(["/state/stale-endpoint.json","/state/ui-endpoint.json"],new([new("panel","dev"),new("settings","dev"),new("hostadmin",Host:"lab")]),false);
            Assert.Equal(["panel","settings","hostadmin"],ui.Activations.Select(a=>a.View));
            using var refused=await http.PostAsync($"http://127.0.0.1:{server.Endpoint.Port}/v1/ui/activate",new StringContent("{}"));
            Assert.Equal(HttpStatusCode.Unauthorized,refused.StatusCode);
            using var badHost=new HttpRequestMessage(HttpMethod.Get,$"http://127.0.0.1:{server.Endpoint.Port}/v1/health"); badHost.Headers.Host="evil.test";
            using var response=await http.SendAsync(badHost); Assert.Equal(421,(int)response.StatusCode);
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
            await client.SendAsync("/state/ui-endpoint.json",new([]),true);
            for(var i=0;i<50 && !ui.Quit;i++) await Task.Delay(10);
            Assert.True(ui.Quit);
            Assert.False(files.FileExists("/state/endpoint.json"));
        }
        Assert.False(files.FileExists("/state/ui-endpoint.json"));
    }
    [Fact]
    public async Task ForwardUriHandoffPreservesInstanceAndId()
    {
        var files=new FakeFileSystem(); var sink=new FakeMessageSink();
        await using var server=await DesktopActivationServer.StartAsync(files,"/ui.json",new FakeUiActivation(),"fixture",messages:sink);
        using var http=new HttpClient(new HttpClientHandler {UseProxy=false,AllowAutoRedirect=false});
        var plan=Activation.Resolve(new(Uri:"construct://forward?instance=dev&id=web"),["dev"],[]);
        await new ActivationClient(files,http,new SystemClock()).SendAsync("/ui.json",plan,false);
        var sent=Assert.Single(sink.Posted); Assert.Equal("dev",sent.Instance); Assert.Equal("openForward",sent.Message.GetProperty("id").GetString()); Assert.Equal("web",sent.Message.GetProperty("forward").GetString());
    }
    [Fact]
    public async Task InProcessBridgeClonesAndRoutesByScopeAndUnsubscribes()
    {
        var bus=new RuntimeMessageBus(); var posted=new List<InstanceMessage>();
        var sink=new InProcessMessageSink(bus,(scope,message,_)=> { posted.Add(new(scope,message)); return Task.CompletedTask; });
        using var stop=new CancellationTokenSource();
        await using var subscriber=sink.Subscribe("dev",stop.Token).GetAsyncEnumerator();
        var pending=subscriber.MoveNextAsync();
        using (var document=JsonDocument.Parse("{\"type\":\"ready\",\"nested\":{\"x\":1}}")) await sink.PostAsync("dev",document.RootElement);
        Assert.Equal(1,posted.Single().Message.GetProperty("nested").GetProperty("x").GetInt32());
        bus.Publish("other",new {type="state",value=1}); Assert.False(pending.IsCompleted);
        bus.Publish("dev",new {type="state",value=2}); Assert.True(await pending); Assert.Equal(2,subscriber.Current.GetProperty("value").GetInt32());
        var next=subscriber.MoveNextAsync(); stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await next);
    }
    private static FakeStateFileSystem Files()
    {
        var files=new FakeStateFileSystem(); files.Files.Roots[FileSystemRoot.LocalAppData]="/local";
        files.Files.Roots[FileSystemRoot.InstallDirectory]="/app"; files.Files.Roots[FileSystemRoot.UserProfile]="/user"; return files;
    }
    [Fact]
    public async Task HeadlessNoInstancesIsSuccessAndDoesNotWriteState()
    {
        var files=Files(); var platform=new FakeSelfTestPlatform();
        var report=await new SelfTest(files,platform,new FakeAudioCapture(),new FakeToastRaiser()).RunAsync();
        Assert.True(report.Ok); Assert.Equal(0,report.ExitCode);
        Assert.Contains(report.Checks,c=>c.Name=="instances" && c.Status=="none");
        Assert.Equal(["health","webview"],platform.Calls); Assert.Empty(files.EnumerateFiles("/local/The-Construct/companion"));
    }
    [Fact]
    public async Task HeadlessMissingWebviewFailsButOfflineVmDoesNot()
    {
        var files=Files(); files.WriteFileAtomic("/local/The-Construct/instances.json","{\"version\":1,\"instances\":{\"agent-vm\":{}}}"u8);
        var platform=new FakeSelfTestPlatform(); var test=new SelfTest(files,platform,new FakeAudioCapture(),new FakeToastRaiser());
        var report=await test.RunAsync(); Assert.True(report.Ok);
        Assert.Contains(report.Checks,c=>c.Name=="ssh:agent-vm" && c.Status=="unreachable" && !c.Required);
        Assert.Equal(1,platform.Calls.Count(c=>c=="ssh:agent-vm"));
        platform.WebViewVersion=null; Assert.False((await test.RunAsync()).Ok);
        Assert.False((await test.RunAsync("missing")).Ok);
    }
    [Fact]
    public async Task MalformedRegistryAndStateFailAndRemainUnchanged()
    {
        var files=Files(); files.WriteFileAtomic("/local/The-Construct/instances.json","bad"u8);
        files.WriteFileAtomic("/local/The-Construct/companion/settings.json","bad"u8);
        var report=await new SelfTest(files,new FakeSelfTestPlatform(),new FakeAudioCapture(),new FakeToastRaiser()).RunAsync();
        Assert.False(report.Ok); Assert.Contains(report.Checks,c=>c.Name=="registry" && c.Status=="failed");
        Assert.Contains(report.Checks,c=>c.Name=="stateFiles" && c.Status=="failed");
        Assert.Equal("bad"u8.ToArray(),files.ReadFile("/local/The-Construct/instances.json"));
    }
    [Fact]
    public async Task HeadlessRealLoopbackHealthDoesNotPublishEndpoint()
    {
        var files=Files(); var runner=new FakeProcessRunner();
        var platform=new DesktopSelfTestPlatform(files,runner,new FakeHypervisorState(),new FakeDesktopProcess(),()=>"fixture");
        Assert.True(await platform.IpcHealthAsync(default)); Assert.Empty(runner.Invocations);
        Assert.Empty(files.EnumerateFiles("/local/The-Construct/companion"));
    }
    [Fact]
    public async Task SelftestSshUsesOneBoundedReadOnlyProbeWithExactArgv()
    {
        var files=Files(); var runner=new FakeProcessRunner(); runner.Results.Enqueue(new(0,"HOSTNAME\tfixture\n"));
        var instance=Instances.DeriveDefaults("dev");
        var platform=new DesktopSelfTestPlatform(files,runner,new FakeHypervisorState(),new FakeDesktopProcess(),()=>"fixture");
        Assert.True(await platform.SshProbeAsync(instance,default));
        var actual=Assert.Single(runner.Invocations); Assert.Equal("ssh",actual.FileName); Assert.Equal(TimeSpan.FromSeconds(15),actual.Timeout);
        var expected=SshArgs.Build(new SshConfiguration(StateJson.String(instance["vmHost"]),StateJson.String(instance["hostAlias"]),KeyName:StateJson.String(instance["keyName"]),ConnectTimeout:8),SshArgs.WrapScriptCommand(GuestScripts.Render("probe")))
            .Select(a=>a=="StrictHostKeyChecking=accept-new" ? "StrictHostKeyChecking=yes" : a);
        Assert.Equal(expected,actual.Arguments); Assert.DoesNotContain("-N",actual.Arguments); Assert.DoesNotContain("-L",actual.Arguments); Assert.DoesNotContain("-R",actual.Arguments);
    }
    [Fact]
    public void RollingLogKeepsFiveFilesAndNeverFormatsExceptionMessage()
    {
        var files=new FakeFileSystem(); var log=new RollingLog(files,new FakeClock(),"/logs",100);
        for(var i=0;i<12;i++) log.Write(DesktopLogEvent.BridgeFailed,new InvalidOperationException("fixture-secret"));
        Assert.Equal(5,files.EnumerateFiles("/logs").Count);
        foreach(var path in files.EnumerateFiles("/logs"))
        {
            var bytes=files.ReadFile(path)!; Assert.True(bytes.Length<=100); Assert.DoesNotContain("fixture-secret",System.Text.Encoding.UTF8.GetString(bytes));
        }
    }
    [Theory]
    [InlineData("startVm","startConnect",null)]
    [InlineData("shutdown","shutdown",null)]
    [InlineData("openForward:web","openForward","web")]
    [InlineData("closeForward:web","closeForward","web")]
    public void TrayCommandsUsePanelProtocol(string input,string id,string? forward)
    {
        var message=DesktopCommand.Message(input);
        Assert.Equal("command",message.GetProperty("type").GetString()); Assert.Equal(id,message.GetProperty("id").GetString());
        if (forward is not null) Assert.Equal(forward,message.GetProperty("forward").GetString());
    }
    [Fact]
    public void TrayMicUsesPersistentToggleAndForwardValidationIsShared()
    {
        Assert.False(DesktopCommand.Message("mic",true).GetProperty("enabled").GetBoolean());
        Assert.Throws<ArgumentException>(()=>DesktopCommand.Message("openForward:a..b"));
        Assert.Throws<ArgumentException>(()=>Activation.Resolve(new(Uri:"construct://forward?instance=dev&id=a..b"),["dev"],[]));
    }
    [Fact]
    public void SnapshotConsumesActualPanelEnvelopeAndClearsHostOffer()
    {
        var snapshot=new DesktopSnapshot(new FakeClock()); snapshot.Select("dev",true);
        snapshot.Apply(JsonSerializer.SerializeToElement(new {type="state",state=new {online=true,vmState="running",hostAdminOffer=new {host="lab",url="https://lab:7462"},forwards=new {items=new[]{new {id="web",label="Web",url="http://localhost:1234"}}}}}));
        Assert.True(snapshot.Current.Online); Assert.True(snapshot.Current.HostAdmin); Assert.Equal("lab_7462",snapshot.AdminHost); Assert.Equal(1,snapshot.Current.ForwardCount);
        snapshot.Apply(JsonSerializer.SerializeToElement(new {type="hostAdminOffer",offer=(object?)null})); Assert.False(snapshot.Current.HostAdmin);
        snapshot.Select("other",true); Assert.Empty(snapshot.Forwards); Assert.Null(snapshot.AdminHost);
    }
}
