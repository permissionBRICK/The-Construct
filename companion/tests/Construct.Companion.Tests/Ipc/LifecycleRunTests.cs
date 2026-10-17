using System.Text;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Lifecycle;
using Construct.Companion.Core.State;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Dispatch;
using Construct.Companion.Host.Ipc;
using Microsoft.Extensions.DependencyInjection;
namespace Construct.Companion.Tests.Ipc;

// One lifecycle run at a time on the host: its state reaches every instance's panel and a second start is refused.
public sealed class LifecycleRunTests
{
    private const string Marker = "/fake/local/The-Construct/lifecycle-running.json";
    private static async Task<HttpTests.Harness> TwoInstances()
    {
        var h = await HttpTests.Harness.Start(runtimeJobs: false);
        h.App.Services.GetRequiredService<CompanionInstances>().Registry.Add("work-vm", new() { ["scriptsDir"] = "/fake/scripts" }).Save(h.Files);
        return h;
    }
    private static void HoldRun(HttpTests.Harness h, int pid = 4242) =>
        h.Files.WriteFileAtomic(Marker, Encoding.UTF8.GetBytes($"{{\"pid\":{pid},\"instance\":\"agent-vm\",\"action\":\"reprovision\",\"startedAt\":\"2026-10-17T10:00:00Z\",\"token\":\"t\"}}"));
    private static List<(string Instance, JsonObject Message)> Messages(IpcEvents.Subscription subscription)
    {
        var seen = new List<(string, JsonObject)>();
        while (subscription.Reader.TryRead(out var item))
            if (item.Event == "message") seen.Add((item.Instance!, JsonNode.Parse(item.Data.GetProperty("message").GetRawText())!.AsObject()));
        return seen;
    }

    [Fact]
    public async Task TheRunReachesEveryInstanceAndEndsWhenItsOwnerIsGone()
    {
        await using var h = await TwoInstances();
        var runs = h.App.Services.GetRequiredService<LifecycleRuns>(); var state = h.App.Services.GetRequiredService<StateAggregation>();
        using var subscription = h.App.Services.GetRequiredService<IpcEvents>().Subscribe();
        HoldRun(h); runs.Publish();
        var started = Messages(subscription).Where(m => StateJson.Text(m.Message["type"]) == "lifecycleRun").ToList();
        Assert.Equal(["agent-vm", "work-vm"], started.Select(m => m.Instance).Order());
        Assert.All(started, m => Assert.Equal("{\"action\":\"reprovision\",\"instance\":\"agent-vm\",\"phase\":\"running\",\"startedAt\":\"2026-10-17T10:00:00Z\"}", m.Message["run"]!.ToJsonString()));
        // A page that switches to the other instance reads the run from that instance's state.
        Assert.Equal("agent-vm", StateJson.Text(state.State("work-vm")["state"]!["lifecycleRun"]!["instance"]));
        runs.Publish();
        Assert.Empty(Messages(subscription));   // published once per change

        // The console was closed: the owner is gone, every panel unlocks.
        h.Get<FakeProcessLiveness, IProcessLiveness>().DeadProcesses.Add(4242); runs.Publish();
        var ended = Messages(subscription);
        Assert.Equal(["agent-vm", "work-vm"], ended.Select(m => m.Instance).Order());
        Assert.All(ended, m => { Assert.Equal("lifecycleRun", StateJson.Text(m.Message["type"])); Assert.Null(m.Message["run"]); });
        Assert.Null(state.State("work-vm")["state"]!["lifecycleRun"]);
    }

    [Theory, InlineData("reprovision"), InlineData("reinstall"), InlineData("updateConstruct"), InlineData("convertToHost")]
    public async Task ASecondStartIsRefusedWhileARunHoldsTheHost(string id)
    {
        await using var h = await TwoInstances();
        foreach (var script in new[] { "Provision-AgentVM.ps1", "Auto-Install.ps1", "Update-Construct.ps1", "service/host/ConvertTo-ConstructHost.ps1" }) h.Files.WriteFileAtomic("/fake/scripts/" + script, "param($Action)"u8);
        HoldRun(h);
        using var subscription = h.App.Services.GetRequiredService<IpcEvents>().Subscribe();
        await h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync("work-vm", new() { ["type"] = "command", ["id"] = id }, CancellationToken.None);
        var refusal = Assert.Single(Messages(subscription), m => m.Message["error"] is not null);
        Assert.Equal(("work-vm", "lifecyclePrepared", id), (refusal.Instance, StateJson.Text(refusal.Message["type"]), StateJson.Text(refusal.Message["id"])));
        Assert.Equal("Reprovision of “agent-vm” is running. Wait until it finishes, then try again.", StateJson.Text(refusal.Message["error"]));
        var launcher = h.Get<FakeLauncher, ILauncher>();
        Assert.Empty(launcher.Detached); Assert.Empty(launcher.Elevated);
        Assert.Empty(h.Get<FakePrompts, IPrompts>().Shown);
    }

    [Fact]
    public async Task ALaunchIsSeenPreparingFromTheOtherInstanceAndAlwaysEnds()
    {
        await using var h = await TwoInstances();
        h.Files.WriteFileAtomic("/fake/scripts/Provision-AgentVM.ps1", "param($Action)"u8);
        var entry = h.App.Services.GetRequiredService<CompanionInstances>().Get("agent-vm");
        var runs = h.App.Services.GetRequiredService<LifecycleRuns>();
        string? seenFromWorkVm = null, refusedWhilePreparing = null;
        ((FakeSshTransport)entry.Ssh).ScriptHandler = (_, _) =>
        {
            // The preflight is running: the other instance sees the launch and cannot start its own.
            seenFromWorkVm ??= h.App.Services.GetRequiredService<StateAggregation>().State("work-vm")["state"]!["lifecycleRun"]?.ToJsonString();
            if (runs.TryBegin("work-vm", "reinstall", out var reason) is null) refusedWhilePreparing = reason;
            return Task.FromResult(new ProcessResult(0, "END\n"));
        };
        using var subscription = h.App.Services.GetRequiredService<IpcEvents>().Subscribe();
        await h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync("agent-vm", new() { ["type"] = "command", ["id"] = "reprovision" }, CancellationToken.None);
        Assert.Equal("{\"action\":\"reprovision\",\"instance\":\"agent-vm\",\"phase\":\"preparing\",\"startedAt\":null}", seenFromWorkVm);
        Assert.Equal("Reprovision of “agent-vm” is starting. Wait until it finishes, then try again.", refusedWhilePreparing);
        Assert.Single(h.Get<FakeLauncher, ILauncher>().Detached);
        // A page subscribed to work-vm never gets agent-vm's lifecyclePrepared, but sees the launch end.
        var forWorkVm = Messages(subscription).Where(m => m.Instance == "work-vm").Select(m => m.Message).ToList();
        Assert.DoesNotContain(forWorkVm, m => StateJson.Text(m["type"]) == "lifecyclePrepared");
        Assert.Equal(["preparing", null], forWorkVm.Where(m => StateJson.Text(m["type"]) == "lifecycleRun").Select(m => StateJson.Text(m["run"]?["phase"])));
        Assert.Null(runs.Current());
        // An export is not a lifecycle run: it is never refused or tracked.
        HoldRun(h);
        await h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync("agent-vm", new() { ["type"] = "command", ["id"] = "exportConfig" }, CancellationToken.None);
        Assert.Equal(2, h.Get<FakeLauncher, ILauncher>().Detached.Count);
    }

    // The launch ends the claim; the console then holds the run through its marker, and closing it (the
    // owner process is gone, no result written) unlocks every instance at once while the result is still awaited.
    [Fact]
    public async Task ClosingTheConstructUpdateConsoleWithoutAResultUnlocksAtOnce()
    {
        await using var h = await TwoInstances();
        h.Files.WriteFileAtomic("/fake/scripts/Update-Construct.ps1", "param($Repo,$Ref)"u8);
        h.Files.WriteFileAtomic("/fake/scripts/.construct-settings.json", "{\"installedCommit\":\"abc\",\"constructRef\":\"main\",\"constructRepo\":\"owner/repo\"}"u8);
        var runs = h.App.Services.GetRequiredService<LifecycleRuns>();
        using var subscription = h.App.Services.GetRequiredService<IpcEvents>().Subscribe();
        await h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync("agent-vm", new() { ["type"] = "command", ["id"] = "updateConstruct" }, CancellationToken.None);
        Assert.Single(h.Get<FakeLauncher, ILauncher>().Detached);
        Assert.Null(runs.Current());
        HoldRun(h, 5151); runs.Publish();
        Assert.Equal("running", StateJson.Text(runs.Current()?["phase"]));
        h.Get<FakeProcessLiveness, IProcessLiveness>().DeadProcesses.Add(5151); runs.Publish();
        Assert.Null(runs.Current());
        var seen = Messages(subscription);
        Assert.Equal([null, "running", null], seen.Where(m => m.Instance == "work-vm" && StateJson.Text(m.Message["type"]) == "lifecycleRun").Select(m => StateJson.Text(m.Message["run"]?["phase"])).Skip(1));
        Assert.DoesNotContain(seen, m => StateJson.Text(m.Message["type"]) == "lifecyclePrepared");   // the result is still awaited
    }

    [Fact]
    public async Task ClosingTheHostConversionConsoleWithoutAResultUnlocksAtOnce()
    {
        await using var h = await TwoInstances();
        var entry = h.App.Services.GetRequiredService<CompanionInstances>().Get("agent-vm");
        var fs = h.Get<FakeFileSystem, IStateFileSystem>(); fs.Roots[FileSystemRoot.UserProfile] = "/fake/user";
        fs.WriteFileAtomic("/fake/user/.ssh/agent_vm_ed25519", "fake-ssh-key"u8);
        fs.WriteFileAtomic("/fake/scripts/service/host/ConvertTo-ConstructHost.ps1", "# installed"u8);
        h.Get<FakeProcessRunner, IProcessRunner>().Handler = _ => new ProcessResult(0, "{\"adminUser\":\"PC\\\\owner\",\"hostName\":\"host.example\",\"ip\":\"192.168.1.2\"}");
        ((FakeSshTransport)entry.Ssh).ScriptHandler = (s, _) => Task.FromResult(new ProcessResult(0, s == "cat /etc/machine-id" ? new string('a', 32) : "192.168.1.2"));
        var prompts = h.Get<FakePrompts, IPrompts>(); prompts.Inputs.Enqueue("host.example"); prompts.Picks.Enqueue(["no"]); prompts.Confirmations.Enqueue(true);
        var runs = h.App.Services.GetRequiredService<LifecycleRuns>();
        using var cancel = new CancellationTokenSource();
        var work = h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync("agent-vm", new() { ["type"] = "command", ["id"] = "convertToHost" }, cancel.Token);
        for (var i = 0; i < 400 && h.Get<FakeLauncher, ILauncher>().Elevated.Count == 0; i++) await Task.Delay(5);
        Assert.Single(h.Get<FakeLauncher, ILauncher>().Elevated);
        Assert.Null(runs.Current());   // launched: the claim is over although the conversion still waits for its result
        HoldRun(h, 6161);
        Assert.Equal("running", StateJson.Text(runs.Current()?["phase"]));
        h.Get<FakeProcessLiveness, IProcessLiveness>().DeadProcesses.Add(6161);
        Assert.Null(runs.Current());
        Assert.False(work.IsCompleted);
        cancel.Cancel();
        try { await work; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task AFailedLaunchStillEndsThePreparingPhase()
    {
        await using var h = await TwoInstances();
        var entry = h.App.Services.GetRequiredService<CompanionInstances>().Get("agent-vm");
        ((FakeSshTransport)entry.Ssh).ScriptHandler = (_, _) => throw new InvalidOperationException("preflight failed");
        await h.App.Services.GetRequiredService<MessageDispatcher>().DispatchAsync("agent-vm", new() { ["type"] = "command", ["id"] = "reprovision" }, CancellationToken.None);
        Assert.Empty(h.Get<FakeLauncher, ILauncher>().Detached);
        Assert.Null(h.App.Services.GetRequiredService<LifecycleRuns>().Current());
        Assert.Null(LifecycleRun.Parse("{\"pid\":7,\"action\":\"reinstall\"}", pid => pid == 7));
        Assert.Null(LifecycleRun.Parse("{ not json", _ => false));
    }
}
