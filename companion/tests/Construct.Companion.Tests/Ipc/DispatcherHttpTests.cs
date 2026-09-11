using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.State;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Ipc;
using Construct.Companion.Core.Drivers;
using Construct.Companion.Core.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using static Construct.Companion.Tests.Ipc.HttpTests;
namespace Construct.Companion.Tests.Ipc;
public sealed class DispatcherHttpTests
{
    [Fact]
    public async Task QueuedDomainRefusalRetainsItsSafeTitle()
    {
        await using var h = await Harness.Start();
        using var stream = await h.Client.GetAsync("/v1/events?instance=agent-vm", HttpCompletionOption.ResponseHeadersRead); using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync()); await reader.ReadLineAsync(); await reader.ReadLineAsync();
        using var response = await h.Post("/v1/instances/agent-vm/messages", new { type = "saveIdlePolicy", policy = new { timeoutMinutes = 10 } }); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var refusal = await Until(reader, d => d["message"]?["error"] is not null); Assert.Contains("Idle policy", refusal["message"]!["error"]!.GetValue<string>());
    }
    [Fact]
    public async Task CheckpointPreferenceProducesVisibleUnsupportedApplyNotice()
    {
        await using var h = await Harness.Start();
        using var stream = await h.Client.GetAsync("/v1/events?instance=agent-vm", HttpCompletionOption.ResponseHeadersRead); using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync()); await reader.ReadLineAsync(); await reader.ReadLineAsync();
        using var save = await h.Post("/v1/instances/agent-vm/messages", new { type = "saveSettings", settings = new { autoCheckpoints = true } }); Assert.Equal(HttpStatusCode.Accepted, save.StatusCode);
        var notice = await Until(reader, d => d["message"]?["error"] is not null);
        Assert.Contains("checkpoint", notice["message"]!["error"]!.GetValue<string>()); Assert.Empty(h.Get<FakeLauncher, ILauncher>().Elevated);
    }
    [Fact]
    public async Task AcceptedDialogSurvivesDisconnectAndReadyDoesNotWait()
    {
        await using var h = await Harness.Start();
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Get<FakePrompts, IPrompts>().ConfirmationHandler = ct => { shown.TrySetResult(); return answer.Task.WaitAsync(ct); };
        using (var client = new HttpClient { BaseAddress = h.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(3) })
        {
            client.DefaultRequestHeaders.Authorization = h.Client.DefaultRequestHeaders.Authorization;
            using var response = await client.PostAsJsonAsync("/v1/instances/agent-vm/messages", new { type = "command", id = "reinstall" });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var stream = await h.Client.GetAsync("/v1/events?instance=agent-vm", HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync()); await reader.ReadLineAsync(); await reader.ReadLineAsync();
        using var ready = await h.Client.PostAsJsonAsync("/v1/instances/agent-vm/messages", new { type = "ready" }); Assert.Equal(HttpStatusCode.Accepted, ready.StatusCode);
        await Until(reader, d => d["message"]?["type"]?.GetValue<string>() == "state");
        Assert.Empty(h.Get<FakeLauncher, ILauncher>().Elevated);
        answer.SetResult(true); await h.App.Services.GetRequiredService<DispatchQueue>().DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(h.Get<FakeLauncher, ILauncher>().Elevated);
    }
    [Theory]
    [InlineData("installGit")][InlineData("startConnect")]
    public async Task DesktopInvocationsArePinned(string id)
    {
        await using var h = await Harness.Start();
        using var response = await h.Post("/v1/instances/agent-vm/messages", new { type = "command", id }); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var expected = id == "installGit" ? PowerShellLaunch.BuildInstallGitLaunch() : VmPower.BuildElevatedCommandLaunch(VmPower.BuildStartCommand("Agent-VM"));
        var launcher = h.Get<FakeLauncher, ILauncher>(); var actual = Assert.Single(launcher.Detached);
        Assert.Equal(expected.File, actual.FileName); Assert.Equal(expected.SpawnArgs, actual.Arguments); Assert.Empty(launcher.Elevated);
    }
    [Fact]
    public async Task ShutdownToleratesSshTeardown()
    {
        await using var h = await Harness.Start(); h.Get<FakePrompts, IPrompts>().Confirmations.Enqueue(true);
        var ssh = h.App.Services.GetRequiredService<CompanionInstances>().Get("agent-vm").Ssh as FakeSshTransport;
        ssh!.ScriptHandler = (_, _) => Task.FromResult(new ProcessResult(255, "", ""));
        using var response = await h.Post("/v1/instances/agent-vm/messages", new { type = "command", id = "shutdown" }); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains(VmPower.ShutdownCommand, ssh.Scripts);
    }
    [Fact]
    public async Task RetargetReusesEntryAndConfigAreaForPreferenceChanges()
    {
        await using var h = await Harness.Start(); var instances = h.App.Services.GetRequiredService<CompanionInstances>(); var entry = instances.Get("agent-vm"); var area = entry.ConfigSync;
        entry.UsagePeriod = "weekly";
        using var change = await h.Client.PutAsJsonAsync("/v1/settings", new { micDevice = "different" });
        await h.App.Services.GetRequiredService<Host.Runtime.RuntimeSupervisor>().RefreshAsync();
        Assert.Same(entry, instances.Get("agent-vm")); Assert.Same(area, entry.ConfigSync); Assert.Equal("weekly", entry.UsagePeriod);
    }
    [Theory]
    [InlineData("default")][InlineData("project.schema")]
    public async Task ReservedProfilesAreRejected(string name)
    { await using var h = await Harness.Start(); using var response = await h.Post("/v1/instances/agent-vm/messages", new { type = "saveProject", name, profile = new { name } }); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); }
    [Theory]
    [InlineData("reprovision", false, "Provision-AgentVM.ps1")]
    [InlineData("exportConfig", false, "Provision-AgentVM.ps1")]
    [InlineData("reinstall", true, "Auto-Install.ps1")]
    [InlineData("redownload", true, "Auto-Install.ps1")]
    public async Task LifecyclePinsArgvAndElevation(string action, bool elevated, string script)
    {
        await using var h = await Harness.Start(); h.Get<FakePrompts, IPrompts>().Confirmations.Enqueue(true);
        using var response = await h.Post("/v1/instances/agent-vm/messages", new { type = "command", id = action }); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var launcher = h.Get<FakeLauncher, ILauncher>(); var invocation = Assert.Single(elevated ? launcher.Elevated : launcher.Detached);
        Assert.Empty(elevated ? launcher.Detached : launcher.Elevated); Assert.Equal("cmd.exe", invocation.FileName);
        var expected = Core.Lifecycle.LifecycleBuilder.BuildInvocation(action, new() { ["instance"] = h.App.Services.GetRequiredService<CompanionInstances>().Get("agent-vm").Definition.DeepClone(), ["settings"] = new InstanceStateStore((IStateFileSystem)h.Files, "agent-vm", "/fake/scripts").ReadSettings(), ["projects"] = new JsonArray(), ["backupDir"] = "/fake/scripts/config", ["instanceParams"] = new JsonArray("InstanceName", "ConfigBranch") })!;
        var expectedLaunch = PowerShellLaunch.BuildHostLaunch("/fake/scripts/" + script, expected["args"]!.AsArray().Select(StateJson.String), elevate: elevated, argSpec: expected["argSpec"] as JsonArray).Invocation("/fake/scripts");
        Assert.Equal(expectedLaunch.Arguments, invocation.Arguments); Assert.False(invocation.CreateNoWindow);
    }
    [Fact]
    public async Task RebuildCancelledBeforeLaunchAndCustomBackupPinned()
    {
        await using var h = await Harness.Start(); var prompts = h.Get<FakePrompts, IPrompts>(); prompts.Confirmations.Enqueue(false);
        using var cancelled = await h.Post("/v1/instances/agent-vm/messages", new { type = "customRebuild", mode = "reinstall", backup = "wipe" }); Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode);
        Assert.Empty(h.Get<FakeLauncher, ILauncher>().Elevated);
        prompts.Confirmations.Enqueue(true);
        using var accepted = await h.Post("/v1/instances/agent-vm/messages", new { type = "customRebuild", mode = "reinstall", backup = "existing" }); Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var args = Assert.Single(h.Get<FakeLauncher, ILauncher>().Elevated).Arguments.ToArray();
        var command = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(args[^1])); Assert.Contains("-BackupMode existing", command);
    }
    [Fact]
    public async Task ProjectRoundTripAndSelectionAreInstanceScoped()
    {
        await using var h = await Harness.Start();
        using var save = await h.Post("/v1/instances/agent-vm/messages", new { type = "saveProject", name = "demo", profile = new { name = "demo", repos = new[] { new { url = "https://example.test/repo.git" } } } }); Assert.Equal(HttpStatusCode.Accepted, save.StatusCode);
        Assert.True(h.Files.FileExists("/fake/local/The-Construct/config/projects/demo.json"));
        h.Get<FakePrompts, IPrompts>().Picks.Enqueue(["demo"]);
        using var select = await h.Post("/v1/instances/agent-vm/messages", new { type = "command", id = "selectProfiles" }); Assert.Equal(HttpStatusCode.Accepted, select.StatusCode);
        var snapshot = await h.Client.GetFromJsonAsync<JsonObject>("/v1/instances/agent-vm/snapshot"); Assert.True(snapshot!["state"]!["state"]!["projects"]![0]!["selected"]!.GetValue<bool>());
        h.Get<FakePrompts, IPrompts>().Confirmations.Enqueue(true);
        using var delete = await h.Post("/v1/instances/agent-vm/messages", new { type = "command", id = "deleteProject", project = "demo" }); Assert.Equal(HttpStatusCode.Accepted, delete.StatusCode); Assert.False(h.Files.FileExists("/fake/local/The-Construct/config/projects/demo.json"));
    }
    [Theory]
    [InlineData("../escape")][InlineData("unsafe/path")][InlineData("bad\\path")]
    public async Task InvalidProjectsNeverWrite(string name)
    { await using var h = await Harness.Start(); using var response = await h.Post("/v1/instances/agent-vm/messages", new { type = "saveProject", name, profile = new { name } }); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); }
    [Fact]
    public async Task UnknownCommandsProduceVisibleRefusalsAndNoLaunches()
    {
        await using var h = await Harness.Start(); using var stream = await h.Client.GetAsync("/v1/events", HttpCompletionOption.ResponseHeadersRead); using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync()); await reader.ReadLineAsync(); await reader.ReadLineAsync();
        foreach (var id in new[] { "unknown", "addProject", "registerThisVm", "removeInstance", "convertToHost", "createFirstVm", "updateConstruct" })
        {
            using var response = await h.Post("/v1/instances/agent-vm/messages", new { type = "command", id }); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var message = await Until(reader, d => d["message"]?["id"]?.GetValue<string>() == id); Assert.NotEmpty(message["message"]!["error"]!.GetValue<string>());
        }
        Assert.Empty(h.Get<FakeLauncher, ILauncher>().Elevated); Assert.Empty(h.Get<FakeLauncher, ILauncher>().Detached);
    }
    [Fact]
    public async Task PendingConversionIsShownWithoutFinishingIt()
    {
        await using var h = await Harness.Start();
        h.Files.WriteFileAtomic("/fake/local/The-Construct/host-conversion.json", "{\"name\":\"agent-vm\",\"resultPath\":\"/fake/result.json\"}"u8);
        h.Files.WriteFileAtomic("/fake/result.json", "{\"ok\":true}"u8);
        var snapshot = await h.Client.GetFromJsonAsync<JsonObject>("/v1/instances/agent-vm/snapshot"); Assert.True(snapshot!["state"]!["state"]!["hostConversionStatus"]!["ready"]!.GetValue<bool>());
        Assert.True(h.Files.FileExists("/fake/local/The-Construct/host-conversion.json")); Assert.Empty(h.Get<FakeLauncher, ILauncher>().Elevated);
    }
}
