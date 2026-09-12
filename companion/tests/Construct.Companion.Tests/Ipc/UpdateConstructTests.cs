using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Lifecycle;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Dispatch;
using Construct.Companion.Host.Runtime;
using Microsoft.Extensions.DependencyInjection;
using static Construct.Companion.Tests.Ipc.HttpTests;
namespace Construct.Companion.Tests.Ipc;
public sealed class UpdateConstructTests
{
    [Fact]
    public async Task UpdateConstructLaunchesTheInstallWideScriptWithTheResultFile()
    {
        await using var h = await Harness.Start();
        h.Files.WriteFileAtomic("/fake/scripts/Update-Construct.ps1", "param($Repo,$Ref)"u8);
        h.Files.WriteFileAtomic("/fake/scripts/.construct-settings.json", "{\"installedCommit\":\"abc\",\"constructRef\":\"main\",\"constructRepo\":\"owner/repo\"}"u8);
        using var response = await h.Post("/v1/instances/agent-vm/messages", new { type = "command", id = "updateConstruct" }); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var launcher = h.Get<FakeLauncher, ILauncher>();
        await Until(() => launcher.Detached.Count == 1);
        var invocation = Assert.Single(launcher.Detached); Assert.Empty(launcher.Elevated);
        var argSpec = new JsonArray(new JsonObject { ["flag"] = "-Repo", ["value"] = "owner/repo" }, new JsonObject { ["flag"] = "-Ref", ["value"] = "main" });
        var expected = PowerShellLaunch.BuildHostLaunch("/fake/scripts/Update-Construct.ps1", ["-Repo", "owner/repo", "-Ref", "main"], argSpec: argSpec).Invocation("/fake/scripts");
        Assert.Equal(expected.Arguments, invocation.Arguments);
        var resultFile = Assert.Contains("CONSTRUCT_UPDATE_RESULT", invocation.EnvironmentOverrides!);
        Assert.StartsWith("/fake/temp/construct-update-", resultFile);
    }
    [Theory, InlineData("ok"), InlineData("fail")]
    public async Task ResultFileOutcomeIsReadAndRemoved(string outcome)
    {
        var files = new FakeFileSystem(new SystemClock()); var clock = new FakeClock();
        var plan = ResultPollingPlan.Create("/temp", "update", 1);
        var poll = MessageDispatcher.PollResultAsync(files, clock, plan, CancellationToken.None);
        clock.Advance(plan.Interval); await Task.Yield();
        files.WriteFileAtomic(plan.File, System.Text.Encoding.UTF8.GetBytes(outcome + "\n"));
        clock.Advance(plan.Interval);
        Assert.Equal(outcome, await poll.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(files.FileExists(plan.File));
    }
    [Fact]
    public async Task UpdateButtonUnblocksOnlyAfterTheResultAndASuccessRefreshesThePanel()
    {
        await using var h = await Harness.Start();
        h.Files.WriteFileAtomic("/fake/scripts/Update-Construct.ps1", "param($Repo,$Ref)"u8);
        h.Files.WriteFileAtomic("/fake/scripts/.construct-settings.json", "{\"installedCommit\":\"abc\",\"constructRef\":\"main\",\"constructRepo\":\"owner/repo\"}"u8);
        using var stream = await h.Client.GetAsync("/v1/events?instance=agent-vm", HttpCompletionOption.ResponseHeadersRead); using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync()); await reader.ReadLineAsync(); await reader.ReadLineAsync();
        using var response = await h.Post("/v1/instances/agent-vm/messages", new { type = "command", id = "updateConstruct" }); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var launcher = h.Get<FakeLauncher, ILauncher>();
        await Until(() => launcher.Detached.Count == 1);
        var resultFile = launcher.Detached[0].EnvironmentOverrides!["CONSTRUCT_UPDATE_RESULT"]!;
        h.App.Services.GetRequiredService<Host.Ipc.IpcEvents>().Companion(new { type = "testBarrier" });
        var before = new List<string>();
        while (true) { var item = await ReadEvent(reader); if (item.Data["type"]?.GetValue<string>() == "testBarrier") break; if (item.Event == "message") before.Add(item.Data["message"]!["type"]!.GetValue<string>()); }
        Assert.DoesNotContain("lifecyclePrepared", before);
        h.Files.WriteFileAtomic(resultFile, "ok\n"u8);
        // After the result: the button unblocks and the panel receives a fresh state (the refresh publishes
        // its parts independently, so only their presence is asserted, not their order).
        var order = new List<string>();
        for (var i = 0; i < 100 && !(order.Contains("lifecyclePrepared") && order.Contains("state")); i++) { var item = await ReadEvent(reader); if (item.Event == "message") order.Add(item.Data["message"]!["type"]!.GetValue<string>()); }
        Assert.Contains("lifecyclePrepared", order); Assert.Contains("state", order);
    }
    [Fact]
    public async Task ResultPollingGivesUpAfterTheTimeout()
    {
        var files = new FakeFileSystem(new SystemClock()); var clock = new FakeClock();
        var plan = ResultPollingPlan.Create("/temp", "update", 1);
        var poll = MessageDispatcher.PollResultAsync(files, clock, plan, CancellationToken.None);
        for (var i = 0; i < 3; i++) { clock.Advance(plan.Timeout); await Task.Yield(); }
        Assert.Equal("timeout", await poll.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
}
