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

public sealed class VmConfigurationTests
{
    [Theory]
    [InlineData("ok")] [InlineData("fail")] [InlineData("timeout")]
    public async Task ResourceLaunchWaitsForElevatedConsoleBeforeShutdownAndUsesResultPlan(string outcome)
    {
        var clock = new FakeClock();
        await using var h = await HttpTests.Harness.Start(s => s.AddSingleton<IClock>(clock), runtimeJobs: false);
        h.Files.WriteFileAtomic("/fake/scripts/Set-AgentVmResources.ps1", "param($VmName,$VmMemoryGB,$VmCpuCount)"u8);
        var entry = h.App.Services.GetRequiredService<CompanionInstances>().Get("agent-vm");
        entry.Store.SaveSettings(new() { ["ram"] = "16", ["cpu"] = "4" });
        var ssh = (FakeSshTransport)entry.Ssh; ssh.ScriptHandler = (_, _) => Task.FromResult(new ProcessResult(0, "{}"));
        h.Get<FakePrompts, IPrompts>().Confirmations.Enqueue(true);
        var task = h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync(entry.Name, new() { ["type"] = "applyVmResources" }, CancellationToken.None);
        var launch = Assert.Single(h.Get<FakeLauncher, ILauncher>().Elevated);
        var expected = PowerShellLaunch.BuildHostLaunch("/fake/scripts/Set-AgentVmResources.ps1", ["-FromPanel", "-VmMemoryGB", "16", "-VmCpuCount", "4"], elevate: true).Invocation("/fake/scripts");
        Assert.Equal(expected.Arguments, launch.Arguments);
        var resultPath = launch.EnvironmentOverrides!["CONSTRUCT_RESOURCES_RESULT"]!;
        Assert.Empty(ssh.Scripts);
        h.Files.WriteFileAtomic(resultPath, "running"u8); clock.Advance(TimeSpan.FromMilliseconds(1500));
        await Until(() => ssh.Scripts.Count == 1);
        Assert.Equal(Core.Drivers.VmPower.ShutdownCommand, ssh.Scripts[0]);
        await Until(() => clock.PendingDelays > 0);
        if (outcome != "timeout") h.Files.WriteFileAtomic(resultPath, Encoding.UTF8.GetBytes(outcome));
        clock.Advance(outcome == "timeout" ? TimeSpan.FromMinutes(21) : TimeSpan.FromMilliseconds(1500));
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(h.Files.FileExists(resultPath));
        Assert.Single(ssh.Scripts, s => s == Core.Drivers.VmPower.ShutdownCommand);
    }
    [Fact]
    public async Task ChangedResourcesAfterConfirmationNeverLaunchOrShutdown()
    {
        await using var h = await HttpTests.Harness.Start(runtimeJobs: false);
        h.Files.WriteFileAtomic("/fake/scripts/Set-AgentVmResources.ps1", "param($VmName)"u8);
        var entry = h.App.Services.GetRequiredService<CompanionInstances>().Get("agent-vm"); entry.Store.SaveSettings(new() { ["cpu"] = "4" });
        h.Get<FakePrompts, IPrompts>().ConfirmationHandler = _ => { entry.Store.SaveSettings(new() { ["cpu"] = "8" }); return Task.FromResult(true); };
        await h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync(entry.Name, new() { ["type"] = "applyVmResources" }, CancellationToken.None);
        Assert.Empty(h.Get<FakeLauncher, ILauncher>().Elevated); Assert.Empty(((FakeSshTransport)entry.Ssh).Scripts);
    }
    [Theory]
    [InlineData("ok", true)] [InlineData("fail", false)] [InlineData("timeout", false)]
    public async Task CheckpointMarkerOnlyRecordsConfirmedSuccess(string outcome, bool marked)
    {
        var clock = new FakeClock();
        await using var h = await HttpTests.Harness.Start(s => s.AddSingleton<IClock>(clock), runtimeJobs: false);
        h.Files.WriteFileAtomic("/fake/scripts/Set-AgentVmCheckpoints.ps1", "param($VmName,$Enabled)"u8);
        h.Get<FakePrompts, IPrompts>().Confirmations.Enqueue(true);
        var entry = h.App.Services.GetRequiredService<CompanionInstances>().Get("agent-vm");
        var task = h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync(entry.Name, new() { ["type"] = "saveSettings", ["settings"] = new JsonObject { ["autoCheckpoints"] = true } }, CancellationToken.None);
        var launch = Assert.Single(h.Get<FakeLauncher, ILauncher>().Elevated);
        var expected = PowerShellLaunch.BuildHostLaunch("/fake/scripts/Set-AgentVmCheckpoints.ps1", ["-FromPanel", "-Enabled", "true"], elevate: true).Invocation("/fake/scripts");
        Assert.Equal(expected.Arguments, launch.Arguments);
        var resultPath = launch.EnvironmentOverrides!["CONSTRUCT_CHECKPOINT_RESULT"]!;
        if (outcome != "timeout") h.Files.WriteFileAtomic(resultPath, Encoding.UTF8.GetBytes(outcome));
        clock.Advance(outcome == "timeout" ? TimeSpan.FromMinutes(11) : TimeSpan.FromMilliseconds(1500));
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(marked ? true : null, entry.Store.ReadAppliedAutoCheckpoints());
        Assert.False(h.Files.FileExists(resultPath));
    }
    [Fact]
    public async Task ReprovisionImportsLooseReposAndPassesTheirSelection()
    {
        await using var h = await HttpTests.Harness.Start(runtimeJobs: false);
        h.Files.WriteFileAtomic("/fake/scripts/Provision-AgentVM.ps1", "param($Action)"u8);
        var entry = h.App.Services.GetRequiredService<CompanionInstances>().Get("agent-vm");
        ((FakeSshTransport)entry.Ssh).ScriptHandler = (_, _) => Task.FromResult(new ProcessResult(0, "api\thttps://example.test/api.git\tmain\nEND\n"));
        await h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync(entry.Name, new() { ["type"] = "command", ["id"] = "reprovision" }, CancellationToken.None);
        Assert.Equal("api", Assert.Single(entry.Store.ReadSelectedProjects())!.GetValue<string>());
        Assert.Single(h.Get<FakeLauncher, ILauncher>().Detached);
        Assert.NotNull(h.App.Services.GetRequiredService<CompanionInstances>().Host.ReadProjectProfile("/fake/local/The-Construct/config", "api"));
    }
    [Theory]
    [InlineData("running", "restart", "")] [InlineData("off", "start", "")]
    [InlineData("saved", "start,restart", "")] [InlineData("paused", "start,restart", "")]
    [InlineData("running", "restart", "job")] [InlineData("running", "", "transport")]
    public async Task RemoteCpuFollowsTheServiceLifecycleBranches(string initialState, string expected, string failure)
    {
        var clock = new FakeClock(); var api = new FakeRemoteApi();
        await using var h = await HttpTests.Harness.Start(s => { s.AddSingleton<IClock>(clock); s.AddSingleton<IRemoteApi>(api); }, runtimeJobs: false);
        h.Files.WriteFileAtomic("/fake/local/The-Construct/instances.json", "{\"version\":1,\"defaultInstance\":\"remote-vm\",\"instances\":{\"agent-vm\":null,\"remote-vm\":{\"backend\":\"hyperv-remote\",\"sshHost\":\"guest.example\",\"scriptsDir\":\"/fake/scripts\",\"service\":{\"url\":\"http://localhost:7462\",\"auth\":\"negotiate\"}}}}"u8);
        var entry = h.App.Services.GetRequiredService<CompanionInstances>().Get("remote-vm"); entry.Store.SaveSettings(new() { ["cpu"] = "4" });
        h.Get<FakePrompts, IPrompts>().Confirmations.Enqueue(true);
        var stateReads = 0;
        api.Handler = request => failure == "transport" && request.Url.AbsolutePath.EndsWith("/cpu") ? throw new System.IO.IOException("boom") : new RemoteResponse(200, System.Text.Json.JsonSerializer.SerializeToElement<object>(request.Url.AbsolutePath.EndsWith("/cpu") ? new { pending = true }
            : request.Url.AbsolutePath.EndsWith("/state") ? new { state = stateReads++ == 0 ? initialState : "running" }
            : request.Url.AbsolutePath.EndsWith("/lifecycle") ? new { jobId = "job1" }
            : request.Url.AbsolutePath.EndsWith("/jobs/job1") ? new { state = failure == "job" ? "failed" : "succeeded", error = failure == "job" ? "boom" : "" } : new { }));
        using var subscription = h.App.Services.GetRequiredService<Host.Ipc.IpcEvents>().Subscribe();
        var task = h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync(entry.Name, new() { ["type"] = "applyVmResources" }, CancellationToken.None);
        if (initialState is "saved" or "paused") { await Until(() => clock.PendingDelays > 0); clock.Advance(TimeSpan.FromSeconds(2)); }
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries), api.Requests.Where(r => r.Url.AbsolutePath.EndsWith("/lifecycle")).Select(r => r.Body!.Value.GetProperty("action").GetString()));
        var cpu = Assert.Single(api.Requests, r => r.Url.AbsolutePath.EndsWith("/cpu"));
        Assert.Equal("PUT", cpu.Method); Assert.Equal(4, cpu.Body!.Value.GetProperty("cpus").GetDouble());
        Assert.Empty(h.Get<FakeLauncher, ILauncher>().Elevated);
        if (failure.Length > 0)
        {
            var messages = new List<string>();
            while (subscription.Reader.TryRead(out var e)) if (e.Event == "message" && e.Data.GetProperty("message").TryGetProperty("error", out var error)) messages.Add(error.GetString()!);
            var notice = Assert.Single(messages);
            Assert.StartsWith("Couldn't apply the vCPU count on localhost: ", notice);
            if (failure == "job") Assert.EndsWith("the restart job ended failed (boom)", notice);
        }
    }
    [Fact]
    public async Task UnchangedCheckpointPreferenceOffersAgainAfterDeclining()
    {
        var clock = new FakeClock();
        await using var h = await HttpTests.Harness.Start(s => s.AddSingleton<IClock>(clock), runtimeJobs: false);
        h.Files.WriteFileAtomic("/fake/scripts/Set-AgentVmCheckpoints.ps1", "param($VmName,$Enabled)"u8);
        h.Get<FakeProcessRunner, IProcessRunner>().Handler = _ => new ProcessResult(0, "VMAUTOCHK=True");
        var prompts = h.Get<FakePrompts, IPrompts>(); prompts.Confirmations.Enqueue(false); prompts.Confirmations.Enqueue(true);
        var dispatcher = h.App.Services.GetRequiredService<MessageDispatcher>();
        JsonObject Save() => new() { ["type"] = "saveSettings", ["settings"] = new JsonObject { ["autoCheckpoints"] = false } };
        await dispatcher.DispatchAsync("agent-vm", Save(), CancellationToken.None);
        Assert.Empty(h.Get<FakeLauncher, ILauncher>().Elevated);
        var task = dispatcher.DispatchAsync("agent-vm", Save(), CancellationToken.None);
        var launch = Assert.Single(h.Get<FakeLauncher, ILauncher>().Elevated);
        h.Files.WriteFileAtomic(launch.EnvironmentOverrides!["CONSTRUCT_CHECKPOINT_RESULT"]!, "ok"u8);
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, prompts.Shown.OfType<ConfirmationPrompt>().Count());
    }
    [Fact]
    public async Task UnreachableImportRequiresContinueAndCancellationNeverLaunches()
    {
        await using var h = await HttpTests.Harness.Start(runtimeJobs: false);
        var entry = h.App.Services.GetRequiredService<CompanionInstances>().Get("agent-vm");
        ((FakeSshTransport)entry.Ssh).ScriptHandler = (_, _) => Task.FromResult(new ProcessResult(255));
        h.Get<FakePrompts, IPrompts>().Confirmations.Enqueue(false);
        await h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync(entry.Name, new() { ["type"] = "command", ["id"] = "reprovision" }, CancellationToken.None);
        Assert.Empty(h.Get<FakeLauncher, ILauncher>().Detached);
    }
    [Fact]
    public async Task ConflictGateBlocksEvenAfterSuccessfulImport()
    {
        await using var h = await HttpTests.Harness.Start(runtimeJobs: false);
        h.Get<FakeProcessRunner, IProcessRunner>().Handler = invocation => new ProcessResult(0,
            invocation.Arguments.Contains("ls-files") ? "100644 abc 1\tprojects/api.json\n" : invocation.Arguments.Contains("--version") ? "git version 2.43.0" : "");
        var entry = h.App.Services.GetRequiredService<CompanionInstances>().Get("agent-vm");
        h.Get<FakePrompts, IPrompts>().ConfirmationHandler = _ => Task.FromResult(true);
        await h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync(entry.Name, new() { ["type"] = "command", ["id"] = "reprovision" }, CancellationToken.None);
        Assert.Empty(h.Get<FakeLauncher, ILauncher>().Detached);
        Assert.Contains(h.Get<FakeProcessRunner, IProcessRunner>().Invocations, i => i.Arguments.Contains("MERGE_HEAD"));
    }
    [Fact]
    public async Task ReprovisionFallsBackToLiveProjectsWhenSelectionIsNotPersisted()
    {
        await using var h = await HttpTests.Harness.Start(runtimeJobs: false);
        var entry = h.App.Services.GetRequiredService<CompanionInstances>().Get("agent-vm");
        ((FakeSshTransport)entry.Ssh).ScriptHandler = (script, _) => Task.FromResult(new ProcessResult(0, script == ProjectImport.ScanScript() ? "END\n" : "PROJECTS\tlive\n"));
        await h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync(entry.Name, new() { ["type"] = "command", ["id"] = "reprovision" }, CancellationToken.None);
        var launch = Assert.Single(h.Get<FakeLauncher, ILauncher>().Detached);
        var command = Encoding.Unicode.GetString(Convert.FromBase64String(launch.Arguments[^1]));
        Assert.Contains("-Projects", command);
        Assert.Contains("live", command);
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(1, timeout.Token);
    }
}
