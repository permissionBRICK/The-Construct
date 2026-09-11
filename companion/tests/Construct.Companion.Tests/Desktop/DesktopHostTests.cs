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
using Construct.Companion.Host.Ipc;
using Construct.Companion.Host.Runtime;
using Construct.Companion.Tests.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static Construct.Companion.Tests.Ipc.HttpTests;
namespace Construct.Companion.Tests.Desktop;

public sealed class DesktopHostTests
{
    [Fact]
    public async Task ActivationHandoffUsesAuthenticatedHttpAndStaleEndpointsAreSkipped()
    {
        await using var h = await Harness.Start();
        var files = h.Files; var clock = h.App.Services.GetRequiredService<IClock>();
        files.WriteFileAtomic("/stale/endpoint.json", "{\"v\":1,\"port\":1,\"token\":\"x\",\"pid\":1}"u8);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var client = new ActivationClient(files, http, clock);
        var fake = new FakeClock(); var stale = new ActivationClient(files, http, fake).SendAsync("/stale/endpoint.json", new([new("panel", "agent-vm")]), false);
        while (!stale.IsCompleted) { fake.Advance(TimeSpan.FromMilliseconds(250)); await Task.Delay(5); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => stale);
        Assert.Empty(h.Get<FakeCompanionDesktop, ICompanionDesktop>().Activations);
        await client.SendAsync(h.EndpointPath, new([new("panel", "agent-vm"), new("settings", "agent-vm"), new("popup")]), false);
        Assert.Equal(["panel", "settings", "popup"], h.Get<FakeCompanionDesktop, ICompanionDesktop>().Activations.Select(a => a.View));
        using var refused = await http.PostAsync($"http://127.0.0.1:{h.Port}/v1/ui/activate", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        using var badHost = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{h.Port}/v1/health"); badHost.Headers.Host = "evil.test";
        using var response = await http.SendAsync(badHost); Assert.Equal(421, (int)response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        await client.SendAsync(h.EndpointPath, new([]), true);
        await h.App.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(files.FileExists(h.EndpointPath));
    }
    [Fact]
    public async Task ForwardUriHandoffPreservesInstanceAndId()
    {
        await using var h = await Harness.Start();
        using var stream = await h.Client.GetAsync("/v1/events?instance=agent-vm", HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync()); await reader.ReadLineAsync(); await reader.ReadLineAsync();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        var plan = Activation.Resolve(new(Uri: "construct://forward?instance=agent-vm&id=web"), ["agent-vm"], []);
        await new ActivationClient(h.Files, http, new SystemClock()).SendAsync(h.EndpointPath, plan, false);
        await h.App.Services.GetRequiredService<DispatchQueue>().DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var refusal = await HttpTests.Until(reader, d => d["message"]?["type"]?.GetValue<string>() == "lifecyclePrepared");
        Assert.Equal("openForward", refusal["message"]!["id"]!.GetValue<string>()); Assert.Equal("agent-vm", refusal["instance"]!.GetValue<string>());
    }
    private static FakeFileSystem Files()
    {
        var files = new FakeFileSystem(); files.Roots[FileSystemRoot.LocalAppData] = "/local";
        files.Roots[FileSystemRoot.InstallDirectory] = "/app"; files.Roots[FileSystemRoot.UserProfile] = "/user"; return files;
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
    public async Task HeadlessProbeUsesNoProcessesAndWritesNoState()
    {
        var files=Files(); var runner=new FakeProcessRunner();
        var platform=new DesktopSelfTestPlatform(files,runner,new FakeHypervisorState(),()=>"fixture",_=>Task.FromResult(true));
        Assert.True(await platform.IpcHealthAsync(default)); Assert.Empty(runner.Invocations);
        Assert.Empty(files.EnumerateFiles("/local/The-Construct/companion"));
    }
    [Fact]
    public async Task SelftestSshUsesOneBoundedReadOnlyProbeWithExactArgv()
    {
        var files=Files(); var runner=new FakeProcessRunner(); runner.Results.Enqueue(new(0,"HOSTNAME\tfixture\n"));
        var instance=Instances.DeriveDefaults("dev");
        var platform=new DesktopSelfTestPlatform(files,runner,new FakeHypervisorState(),()=>"fixture",_=>Task.FromResult(true));
        Assert.True(await platform.SshProbeAsync(instance,default));
        var actual=Assert.Single(runner.Invocations); Assert.Equal("ssh",actual.FileName); Assert.Equal(TimeSpan.FromSeconds(15),actual.Timeout);
        var expected=SshArgs.Build(new SshConfiguration(StateJson.String(instance["vmHost"]),StateJson.String(instance["hostAlias"]),KeyName:StateJson.String(instance["keyName"]),ConnectTimeout:8),SshArgs.WrapScriptCommand(GuestScripts.Render("probe")))
            .Select(a=>a=="StrictHostKeyChecking=accept-new" ? "StrictHostKeyChecking=yes" : a);
        Assert.Equal(expected,actual.Arguments); Assert.DoesNotContain("-N",actual.Arguments); Assert.DoesNotContain("-L",actual.Arguments); Assert.DoesNotContain("-R",actual.Arguments);
    }
    [Fact]
    public void RollingLogIsBestEffortWhenTheDirectoryIsDenied()
    {
        var files = new FakeFileSystem { WriteFailure = new UnauthorizedAccessException("denied") }; var log = new RollingLog(files, new FakeClock(), "/logs");
        log.Write(DesktopLogEvent.SettingsFailed, new UnauthorizedAccessException("denied"));
        files.WriteFailure = new IOException("disk full"); log.Write(DesktopLogEvent.Stopped);
        Assert.Empty(files.EnumerateFiles("/logs"));
        files.WriteFailure = null; log.Write(DesktopLogEvent.Started); Assert.Single(files.EnumerateFiles("/logs"));
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
