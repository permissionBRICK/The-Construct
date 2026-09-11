using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Lifecycle;
using Construct.Companion.Core.State;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Dispatch;
using Microsoft.Extensions.DependencyInjection;
namespace Construct.Companion.Tests.Ipc;

public sealed class HostConversionWorkflowTests
{
    private const string Pending="/fake/local/The-Construct/host-conversion.json";
    private static JsonObject Plan(CompanionInstance entry)=>new() { ["id"]="0123456789abcdef0123456789abcdef",["name"]=entry.Name,["adminUser"]="PC\\owner",["publicHost"]="host.example",["scriptsDir"]="/fake/scripts",["fingerprint"]=Instances.TargetFingerprint(entry.Definition),["publicKeyXml"]="<RSAKeyValue/>",["resultPath"]="/fake/result.json" };
    private static JsonObject Result(JsonObject plan)=>new() { ["ok"]=true,["id"]=plan["id"]!.DeepClone(),["name"]=plan["name"]!.DeepClone(),["owner"]=plan["adminUser"]!.DeepClone(),["url"]="https://host.example:7462",["publicHost"]="host.example",["sshPort"]=2222,["fingerprint"]=new string('a',64),["encryptedToken"]="ciphertext" };
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExistingResultRequiresExplicitFinishBeforeCredentialsOrRegistryWrites(bool finish)
    {
        await using var h=await HttpTests.Harness.Start(runtimeJobs:false);
        var instances=h.App.Services.GetRequiredService<CompanionInstances>(); var entry=instances.Get("agent-vm");var plan=Plan(entry);
        h.Files.WriteFileAtomic(Pending,StateJson.Bytes(plan));h.Files.WriteFileAtomic("/fake/result.json",StateJson.Bytes(Result(plan)));
        await h.App.Services.GetRequiredService<ITokenStore>().WriteAsync("companion-conversion-"+StateJson.String(plan["id"]),new Secret("test-private-key"));
        h.Get<FakePrompts,IPrompts>().Confirmations.Enqueue(finish);
        var api=h.Get<FakeRemoteApi,IRemoteApi>();api.Fingerprint=new string('a',64);
        api.Handler=r=>new(200,JsonSerializer.SerializeToElement<object>(r.Url.AbsolutePath.EndsWith("/state",StringComparison.Ordinal)?new {state="running"}:new {name="PC\\owner",role="admin"}));
        await h.App.Services.GetRequiredService<HostConversionWorkflow>().RunAsync(entry,CancellationToken.None);
        Assert.Equal(!finish,h.Files.FileExists(Pending));
        Assert.Equal(finish?"hyperv-remote":"hyperv-local",StateJson.Text(instances.Registry.ByName["agent-vm"]["backend"]));
        Assert.Equal(finish?1:0,h.Get<FakeHostConversionCrypto,IHostConversionCrypto>().Decryptions);
        Assert.Empty(h.Get<FakeLauncher,ILauncher>().Elevated);
        if (!finish) Assert.Empty(api.Requests);
        var confirmation=Assert.IsType<ConfirmationPrompt>(Assert.Single(h.Get<FakePrompts,IPrompts>().Shown));Assert.Equal("Finish host conversion",confirmation.Action);
    }
    [Fact]
    public async Task CancellingFinishClearsThePanelPreparationIndicator()
    {
        await using var h=await HttpTests.Harness.Start(runtimeJobs:false);var entry=h.App.Services.GetRequiredService<CompanionInstances>().Get("agent-vm");var plan=Plan(entry);
        h.Files.WriteFileAtomic(Pending,StateJson.Bytes(plan));h.Files.WriteFileAtomic("/fake/result.json",StateJson.Bytes(Result(plan)));h.Get<FakePrompts,IPrompts>().Confirmations.Enqueue(false);
        using var subscription=h.App.Services.GetRequiredService<Host.Ipc.IpcEvents>().Subscribe();
        await h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync("agent-vm",new(){["type"]="command",["id"]="convertToHost"},CancellationToken.None);
        var cleared=false;while(subscription.Reader.TryRead(out var item)) if(item.Data.TryGetProperty("message",out var message) && message.TryGetProperty("type",out var type) && type.GetString()=="lifecyclePrepared") cleared=message.GetProperty("id").GetString()=="convertToHost";
        Assert.True(cleared);Assert.True(h.Files.FileExists(Pending));
    }
    [Fact]
    public async Task RetryKeepsTheSavedAwakeChoice()
    {
        await using var h=await HttpTests.Harness.Start(runtimeJobs:false);var entry=h.App.Services.GetRequiredService<CompanionInstances>().Get("agent-vm");var plan=Plan(entry);plan["keepAwake"]=true;
        h.Files.WriteFileAtomic(Pending,StateJson.Bytes(plan));h.Files.WriteFileAtomic("/fake/result.json","{\"ok\":false}"u8);
        h.Files.WriteFileAtomic("/fake/scripts/service/host/ConvertTo-ConstructHost.ps1","# installed"u8);
        var prompts=h.Get<FakePrompts,IPrompts>();prompts.Inputs.Enqueue("host.example");prompts.Picks.Enqueue(null);
        await h.App.Services.GetRequiredService<HostConversionWorkflow>().RunAsync(entry,CancellationToken.None);
        var pick=Assert.Single(prompts.Shown.OfType<PickPrompt>());Assert.True(pick.Items.Single(i=>i.Id=="yes").Picked);Assert.False(pick.Items.Single(i=>i.Id=="no").Picked);
        Assert.Empty(h.Get<FakeLauncher,ILauncher>().Elevated);
    }
    [Fact]
    public async Task ResultIdentityMismatchCannotReadSecretOrMutateRegistry()
    {
        await using var h=await HttpTests.Harness.Start(runtimeJobs:false); var instances=h.App.Services.GetRequiredService<CompanionInstances>();var entry=instances.Get("agent-vm");var plan=Plan(entry);var result=Result(plan);result["owner"]="someone-else";
        h.Files.WriteFileAtomic(Pending,StateJson.Bytes(plan));h.Files.WriteFileAtomic("/fake/result.json",StateJson.Bytes(result));h.Get<FakePrompts,IPrompts>().Confirmations.Enqueue(true);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>h.App.Services.GetRequiredService<HostConversionWorkflow>().RunAsync(entry,CancellationToken.None));
        Assert.Equal(0,h.Get<FakeHostConversionCrypto,IHostConversionCrypto>().Decryptions);Assert.True(h.Files.FileExists(Pending));Assert.Equal("hyperv-local",StateJson.Text(instances.Registry.ByName["agent-vm"]["backend"]));
    }
    [Fact]
    public async Task NewConversionPersistsOnlyPublicPlanAndPinsElevatedLaunch()
    {
        await using var h=await HttpTests.Harness.Start(runtimeJobs:false);var instances=h.App.Services.GetRequiredService<CompanionInstances>();var entry=instances.Get("agent-vm");
        var fs=h.Get<FakeFileSystem,IStateFileSystem>();fs.Roots[FileSystemRoot.UserProfile]="/fake/user";
        fs.WriteFileAtomic("/fake/user/.ssh/agent_vm_ed25519","fake-ssh-key"u8);
        fs.WriteFileAtomic("/fake/scripts/service/host/ConvertTo-ConstructHost.ps1","# installed"u8);
        h.Get<FakeProcessRunner,IProcessRunner>().Handler=_=>new ProcessResult(0,"{\"adminUser\":\"PC\\\\owner\",\"hostName\":\"host.example\",\"ip\":\"192.168.1.2\"}");
        ((FakeSshTransport)entry.Ssh).ScriptHandler=(s,_)=>Task.FromResult(new ProcessResult(0,s=="cat /etc/machine-id"?new string('a',32):"192.168.1.2"));
        var prompts=h.Get<FakePrompts,IPrompts>();prompts.Inputs.Enqueue("host.example");prompts.Picks.Enqueue(["no"]);prompts.Confirmations.Enqueue(true);
        using var cancel=new CancellationTokenSource();var work=h.App.Services.GetRequiredService<HostConversionWorkflow>().RunAsync(entry,cancel.Token);
        for(var i=0;i<200 && h.Get<FakeLauncher,ILauncher>().Elevated.Count==0;i++) await Task.Delay(5);
        var launch=Assert.Single(h.Get<FakeLauncher,ILauncher>().Elevated);var plan=StateJson.ReadObject(fs,Pending)!;
        Assert.Equal(HostConversionLaunch.Build(plan).Arguments,launch.Arguments);
        Assert.NotNull(await h.App.Services.GetRequiredService<ITokenStore>().ReadAsync("companion-conversion-"+StateJson.String(plan["id"])));
        Assert.False(Encoding.UTF8.GetString(fs.ReadFile(Pending)!).Contains("PRIVATE",StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HostConversionLaunch.MachineIdentity().Arguments,h.Get<FakeProcessRunner,IProcessRunner>().Invocations.Last().Arguments);
        cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>work);
        Assert.True(fs.FileExists(Pending));
    }
}
