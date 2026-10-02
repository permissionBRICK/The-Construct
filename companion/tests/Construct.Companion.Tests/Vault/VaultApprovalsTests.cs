using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Core.Vault;
using Construct.Companion.Fakes;
using static Construct.Companion.Tests.Runtime.ProcessSupervisorTests;
namespace Construct.Companion.Tests.Vault;

// The tray pop-out's model (VaultApprovals): which approvals it lists, when it shows and hides itself, when
// Approve is armed, and how the page's requests are checked against the pending records.
public sealed class VaultApprovalsTests
{
    private const string Value = "SENTINEL-popout-3f9a";
    private sealed class Harness
    {
        public FakeClock Clock { get; } = new();
        public VaultService Vault { get; }
        public VaultApprovals Model { get; }
        public Harness()
        {
            Clock.Advance(TimeSpan.FromDays(20000));
            Vault = new(new VaultStore(new FakeFileSystem(Clock), new FakeDataProtection(), "/vault.dat"), new FakePrompts(), new FakeToastRaiser(), Clock);
            Vault.Save(new("github-token", "GitHub token for CI", "ci-bot", new Secret(Value)));
            Model = new(Vault, Clock);
        }
        private int ids;
        public Task<JsonObject> Ask(string instance, string op, TimeSpan? deadline = null)
        {
            var line = new JsonObject { ["v"] = 1, ["id"] = $"1700000000000-{++ids}-9", ["op"] = op, ["names"] = new JsonArray("github-token"), ["reason"] = "release" };
            if (deadline is { } after) line["deadline"] = (Clock.UtcNow + after).ToUnixTimeMilliseconds();
            return Vault.HandleAsync(instance, VaultProtocol.ParseRequest(line.ToJsonString()).Request!);
        }
        // A hosted VM's approval as VaultHosts lists it; forward plays the host's reply.
        public Task Host(string id, Func<bool, Task<VaultDecision>> forward, CancellationToken token = default) => Vault.ApproveHostAsync(
            new("dev", "dev", "request", ["github-token"], null, "host.example_7462", "host.example", id, Clock.UtcNow),
            new("Key vault — access request", "The VM “dev” asks for access to:\n• github-token — CI", "Approve"), Clock.UtcNow + TimeSpan.FromMinutes(10), forward, token);
        public async Task Pending(int count) => await Eventually(() => Vault.PendingApprovals().Count == count);
        public JsonArray Items() => Model.StateMessage()["items"]!.AsArray();
        public static VaultApprovalsCommand Decide(string id, string decision) => VaultApprovals.Parse(JsonSerializer.SerializeToElement(new { type = "approvals.decide", id, decision }));
    }

    [Fact]
    public async Task ThePopOutShowsWhenAnApprovalArrivesAndHidesWhenNoneIsLeft()
    {
        var h = new Harness();
        Assert.Equal(new VaultApprovalsChange(false, true, 0), h.Model.Sync());
        var first = h.Ask("dev", "get", TimeSpan.FromMinutes(5)); await h.Pending(1);
        Assert.Equal(new VaultApprovalsChange(true, false, 1), h.Model.Sync());
        Assert.Equal(new VaultApprovalsChange(false, false, 1), h.Model.Sync()); // hidden by the user: stays hidden
        var second = h.Ask("build", "request"); await h.Pending(2);
        Assert.Equal(new VaultApprovalsChange(true, false, 2), h.Model.Sync()); // a new one shows it again
        Assert.Equal(VaultDecision.Decided, await h.Vault.DecideAsync(h.Vault.PendingApprovals()[1].Id, false));
        Assert.Equal("denied", (await second)["status"]!.GetValue<string>());
        Assert.Equal(new VaultApprovalsChange(false, false, 1), h.Model.Sync());
        await Eventually(() => h.Clock.PendingDelays > 0);
        h.Clock.Advance(TimeSpan.FromMinutes(5)); // the agent stopped waiting
        await first;
        Assert.Equal(new VaultApprovalsChange(false, true, 0), h.Model.Sync());
        Assert.Equal(0, h.Model.Count);
    }

    [Fact]
    public async Task ItemsCarryTheTextsWhereAndTimeLeftButNoValue()
    {
        var h = new Harness();
        _ = h.Ask("dev", "get", TimeSpan.FromMinutes(5)); await h.Pending(1);
        _ = h.Host("h1", _ => Task.FromResult(VaultDecision.Decided)); await h.Pending(2);
        var state = h.Model.StateMessage();
        Assert.Equal("approvals.state", state["type"]!.GetValue<string>());
        Assert.DoesNotContain(Value, state.ToJsonString()); Assert.DoesNotContain("ci-bot", state.ToJsonString());
        var (local, hosted) = (state["items"]![0]!, state["items"]![1]!);
        Assert.Equal(h.Vault.PendingApprovals()[0].Id, local["id"]!.GetValue<string>());
        Assert.Equal(("dev", "this PC", "Key vault — one-time access", "Allow once", "Deny", "5 min left"), (local["vm"]!.GetValue<string>(), local["where"]!.GetValue<string>(),
            local["title"]!.GetValue<string>(), local["action"]!.GetValue<string>(), local["deny"]!.GetValue<string>(), local["left"]!.GetValue<string>()));
        Assert.Contains("“release”", local["message"]!.GetValue<string>()); Assert.Equal("github-token", local["names"]![0]!.GetValue<string>());
        Assert.Equal(("on host.example", "10 min left"), (hosted["where"]!.GetValue<string>(), hosted["left"]!.GetValue<string>()));
        Assert.Equal(1000, local["armIn"]!.GetValue<long>());

        h.Clock.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal(600, h.Items()[0]!["armIn"]!.GetValue<long>()); // armed from when the page first got it
        _ = h.Ask("build", "delete"); await h.Pending(3);
        Assert.Equal([600L, 600L, 1000L], h.Items().Select(i => i!["armIn"]!.GetValue<long>()));
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.All(h.Items().Take(2), i => Assert.Equal(0, i!["armIn"]!.GetValue<long>()));
        Assert.Equal(["dev", "dev", "build"], h.Items().Select(i => i!["vm"]!.GetValue<string>())); // in the order they arrived
        Assert.Equal("", VaultApprovals.Left(null, h.Clock.UtcNow));
        Assert.Equal("ending", VaultApprovals.Left(h.Clock.UtcNow, h.Clock.UtcNow));
        Assert.Equal("31 s left", VaultApprovals.Left(h.Clock.UtcNow + TimeSpan.FromSeconds(30.2), h.Clock.UtcNow));
        Assert.Equal("2 min left", VaultApprovals.Left(h.Clock.UtcNow + TimeSpan.FromSeconds(61), h.Clock.UtcNow));
    }

    [Fact]
    public async Task ApproveWaitsForItsArmingWhileDenyDoesNot()
    {
        var h = new Harness();
        var get = h.Ask("dev", "get"); await h.Pending(1);
        var id = h.Vault.PendingApprovals()[0].Id;
        Assert.Equal("Approve is not available yet. Try again.", await h.Model.DecideAsync(Harness.Decide(id, "approve"), default)); // the page never showed it
        h.Model.StateMessage(); h.Clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal("Approve is not available yet. Try again.", await h.Model.DecideAsync(Harness.Decide(id, "approve"), default));
        h.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Null(await h.Model.DecideAsync(Harness.Decide(id, "approve"), default));
        Assert.Equal("ok", (await get)["status"]!.GetValue<string>());
        Assert.Equal("This request was already answered or has ended.", await h.Model.DecideAsync(Harness.Decide(id, "deny"), default));

        var delete = h.Ask("dev", "delete"); await h.Pending(1);
        Assert.Null(await h.Model.DecideAsync(Harness.Decide(h.Vault.PendingApprovals()[0].Id, "deny"), default)); // Deny needs no arming
        Assert.Equal("denied", (await delete)["status"]!.GetValue<string>());
        Assert.DoesNotContain(h.Vault.Activity(), a => a.Text.Contains("another app", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHostApprovalsAnswerReportsTheHostsReply()
    {
        var h = new Harness();
        foreach (var (reply, notice) in new (VaultDecision, string?)[] { (VaultDecision.Decided, null), (VaultDecision.AlreadyDecided, "This request was answered elsewhere first."),
            (VaultDecision.NotFound, "This request was already answered or has ended."), (VaultDecision.Failed, "The host did not take your answer. The Key Vault's Activity tab says why.") })
        {
            bool? sent = null;
            var wait = h.Host("h-" + reply, approved => { sent = approved; return Task.FromResult(reply); }); await h.Pending(1);
            h.Model.StateMessage(); h.Clock.Advance(VaultApprovals.ArmDelay);
            Assert.Equal(notice, await h.Model.DecideAsync(Harness.Decide(h.Vault.PendingApprovals()[0].Id, "approve"), default));
            await wait; Assert.True(sent);
        }
    }

    [Theory]
    [InlineData("""{"type":"vault.copySecret","name":"github-token"}""")]
    [InlineData("""{"type":"approvals.reveal"}""")]
    [InlineData("""{"type":"approvals.decide","decision":"approve"}""")]
    [InlineData("""{"type":"approvals.decide","id":"a/b","decision":"approve"}""")]
    [InlineData("""{"type":"approvals.decide","id":"abc","decision":"yes"}""")]
    [InlineData("""{"type":"approvals.size","height":-1}""")]
    [InlineData("""{"type":"approvals.size","height":"300"}""")]
    [InlineData("""["approvals.ready"]""")]
    public void MalformedPageRequestsAreRefused(string json) =>
        Assert.Throws<ArgumentException>(() => VaultApprovals.Parse(JsonDocument.Parse(json).RootElement));

    [Fact]
    public void PageRequestsAndWindowSizes()
    {
        VaultApprovalsCommand Parse(string json) => VaultApprovals.Parse(JsonDocument.Parse(json).RootElement);
        Assert.Equal(new VaultApprovalsCommand("decide", "abc", true), Parse("""{"type":"approvals.decide","id":"abc","decision":"approve"}"""));
        Assert.Equal(new VaultApprovalsCommand("decide", "abc", false), Parse("""{"type":"approvals.decide","id":"abc","decision":"deny"}"""));
        Assert.Equal(new VaultApprovalsCommand("size", Height: 312), Parse("""{"type":"approvals.size","height":312}"""));
        foreach (var action in new[] { "ready", "hide", "openVault" }) Assert.Equal(new VaultApprovalsCommand(action), Parse($$"""{"type":"approvals.{{action}}"}"""));
        Assert.Equal(VaultApprovals.MinHeight, VaultApprovals.WindowHeight(20, 96));
        Assert.Equal(468, VaultApprovals.WindowHeight(312, 144));
        Assert.Equal(TrayModel.PopupSize(120).Height, VaultApprovals.WindowHeight(5000, 120)); // never taller than the launcher popup
        Assert.Equal("approvals", WebViewDocument.Surface("approvals")); Assert.Equal("Construct Key Vault Requests", WebViewDocument.Title("approvals"));
    }

    [Fact]
    public void TheTrayBringsThePopOutBackWhileApprovalsWait()
    {
        var state = new TrayState("dev", true);
        Assert.DoesNotContain(TrayModel.Menu(state, ["dev"], [], true, false), m => m.Id == "approvals");
        Assert.Equal(new MenuEntry("approvals", "Key vault request (1)…"), TrayModel.Menu(state, ["dev"], [], true, false, 1)[0]);
        Assert.Equal("Key vault requests (3)…", TrayModel.Menu(state, ["dev"], [], true, false, 3)[0].Text);
        Assert.Equal("approvals", TrayModel.LeftClickView(2, approvalsVisible: false));
        Assert.Equal("popup", TrayModel.LeftClickView(2, approvalsVisible: true));
        Assert.Equal("popup", TrayModel.LeftClickView(0, approvalsVisible: false));
    }
}
