using System.Text;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Lifecycle;
using Construct.Companion.Core.State;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Dispatch;
using Microsoft.Extensions.DependencyInjection;
namespace Construct.Companion.Tests.Ipc;

public sealed class InstanceWorkflowTests
{
    [Theory]
    [InlineData("WORK-VM.mshome.net", "work-vm")]
    [InlineData("10.0.0.5", "10")]
    public async Task RegistrationSuggestsValidatedFirstHostLabel(string host, string suggested)
    {
        await using var h = await HttpTests.Harness.Start(runtimeJobs: false);
        var prompts = h.Get<FakePrompts, IPrompts>(); prompts.Inputs.Enqueue(host); prompts.Inputs.Enqueue(null);
        await h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync("agent-vm", new() { ["type"] = "command", ["id"] = "registerThisVm" }, CancellationToken.None);
        Assert.Equal(suggested, Assert.IsType<InputPrompt>(prompts.Shown[1]).Value);
    }
    [Theory]
    [InlineData("param([ValidateSet('install','remove-instance')] [string]$Action)", true)]
    [InlineData("param([ValidateSet('install')] [string]$Action)", false)]
    [InlineData("# [ValidateSet('remove-instance')] [string]$Action", false)]
    [InlineData("<# [ValidateSet('remove-instance')] [string]$Action #>", false)]
    public void RemovalRequiresRealInstallerActionDeclaration(string source, bool supported) => Assert.Equal(supported, LifecycleBuilder.ScriptSupportsRemoveInstance(source));
    [Fact]
    public async Task RegisterPromptsForHostAndCanonicalIdentityThenSelectsIt()
    {
        await using var h = await HttpTests.Harness.Start(runtimeJobs: false);
        var prompts = h.Get<FakePrompts, IPrompts>(); prompts.Inputs.Enqueue("work-vm.mshome.net"); prompts.Inputs.Enqueue("work-vm");
        await h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync("agent-vm", new() { ["type"] = "command", ["id"] = "registerThisVm" }, CancellationToken.None);
        var entry = h.App.Services.GetRequiredService<CompanionInstances>().Registry.ByName["work-vm"];
        Assert.Equal("work-vm.mshome.net", StateJson.Text(entry["vmHost"]));
        Assert.Equal("construct_work-vm_ed25519", StateJson.Text(entry["keyName"]));
        Assert.Equal("vm-work-vm", StateJson.Text(entry["configBranch"]));
        Assert.Equal("work-vm", h.App.Services.GetRequiredService<Host.Ipc.IpcSettings>().Read().ActiveInstance);
    }
    [Fact]
    public async Task RegistrationRefusesMismatchedIdentityWithoutWriting()
    {
        await using var h = await HttpTests.Harness.Start(runtimeJobs: false);
        var original = h.Files.ReadFile("/fake/local/The-Construct/instances.json");
        var prompts = h.Get<FakePrompts, IPrompts>(); prompts.Inputs.Enqueue("somewhere.example"); prompts.Inputs.Enqueue("work-vm");
        await h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync("agent-vm", new() { ["type"] = "command", ["id"] = "registerThisVm" }, CancellationToken.None);
        Assert.Equal(original, h.Files.ReadFile("/fake/local/The-Construct/instances.json"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RemoveLocalOrRemoteKeepVmUsesNonElevatedInstaller(bool remote)
    {
        await using var h = await HttpTests.Harness.Start(runtimeJobs: false);
        var instances = h.App.Services.GetRequiredService<CompanionInstances>();
        instances.Registry.Add("work-vm", remote ? new() { ["backend"] = "hyperv-remote", ["sshHost"] = "guest.example", ["scriptsDir"] = "/fake/scripts", ["service"] = new JsonObject { ["url"] = "http://localhost:7462", ["auth"] = "negotiate" } } : new() { ["scriptsDir"] = "/fake/scripts" }).Save(h.Files);
        h.Files.WriteFileAtomic("/fake/scripts/Auto-Install.ps1", "param([ValidateSet('install','remove-instance')] [string]$Action,$InstanceName,$KeepVm)"u8);
        var prompts = h.Get<FakePrompts, IPrompts>();
        if (remote) { prompts.Picks.Enqueue(["keep"]); prompts.Inputs.Enqueue("work-vm"); } else prompts.Confirmations.Enqueue(true);
        await h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync("work-vm", new() { ["type"] = "command", ["id"] = "removeInstance" }, CancellationToken.None);
        var launcher = h.Get<FakeLauncher, ILauncher>(); var actual = Assert.Single(launcher.Detached); Assert.Empty(launcher.Elevated);
        var plan = LifecycleBuilder.BuildInvocation("removeInstance", new() { ["instance"] = instances.Registry.ByName["work-vm"].DeepClone(), ["confirmation"] = remote ? "work-vm" : "" })!;
        var args = plan["args"]!.AsArray().Select(StateJson.String).ToList(); var pairs = plan["argSpec"]!.AsArray();
        if (remote) { args.Add("-KeepVm"); pairs.Add(new JsonObject { ["flag"] = "-KeepVm" }); }
        Assert.Equal(PowerShellLaunch.BuildHostLaunch("/fake/scripts/Auto-Install.ps1", args, argSpec: pairs).SpawnArgs, actual.Arguments);
    }
    [Theory]
    [InlineData(0, true)] [InlineData(3, true)] [InlineData(1, false)]
    public async Task AddProjectClonesAndOpensOnlyTheCapturedInstance(int result, bool opens)
    {
        await using var h = await HttpTests.Harness.Start(runtimeJobs: false);
        var prompts = h.Get<FakePrompts, IPrompts>(); prompts.Inputs.Enqueue(" https://example.test/repo.git "); if (result == 3) prompts.Confirmations.Enqueue(true);
        var ssh = (FakeSshTransport)h.App.Services.GetRequiredService<CompanionInstances>().Get("agent-vm").Ssh;
        ssh.ScriptHandler = (_, _) => Task.FromResult(new ProcessResult(result));
        await h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync("agent-vm", new() { ["type"] = "command", ["id"] = "addProject" }, CancellationToken.None);
        Assert.Equal(InstanceWorkflowPlans.CloneScript("https://example.test/repo.git", "repo"), ssh.Scripts[0]);
        var opened = h.Get<FakeLauncher, ILauncher>().Opened;
        if (opens) Assert.Equal("vscode://vscode-remote/ssh-remote+agent-vm/root/repos/repo", Assert.Single(opened)); else Assert.Empty(opened);
    }
}
