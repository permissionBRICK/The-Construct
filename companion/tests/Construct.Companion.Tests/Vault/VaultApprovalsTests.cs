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
    public async Task ThePopOutShowsWhileApprovalsWaitAndStaysClosedUntilANewOne()
    {
        var h = new Harness();
        Assert.Equal(new VaultApprovalsVisibility(false, 0), h.Model.Sync());
        var first = h.Ask("dev", "get", TimeSpan.FromMinutes(5)); await h.Pending(1);
        Assert.Equal(new VaultApprovalsVisibility(true, 1), h.Model.Sync()); // no other app reported: at once
        Assert.Equal(new VaultApprovalsVisibility(true, 1), h.Model.Sync());
        h.Model.Dismiss(); // its ×
        Assert.Equal(new VaultApprovalsVisibility(false, 1), h.Model.Sync()); // closed by the user: stays hidden
        var second = h.Ask("build", "request"); await h.Pending(2);
        Assert.Equal(new VaultApprovalsVisibility(true, 2), h.Model.Sync()); // a new one shows it again
        Assert.Equal(2, h.Items().Count); // listing both
        Assert.Equal(VaultDecision.Decided, await h.Vault.DecideAsync(h.Vault.PendingApprovals()[1].Id, false));
        Assert.Equal("denied", (await second)["status"]!.GetValue<string>());
        Assert.Equal(new VaultApprovalsVisibility(false, 1), h.Model.Sync()); // only the closed one is left
        await Eventually(() => h.Clock.PendingDelays > 0);
        h.Clock.Advance(TimeSpan.FromMinutes(5)); // the agent stopped waiting
        await first;
        Assert.Equal(new VaultApprovalsVisibility(false, 0), h.Model.Sync());
        Assert.Equal(0, h.Model.Count);
        Assert.False(h.Model.Open()); // nothing to bring up
    }

    // ── T3 Code Desktop shows them (POST /v1/vault/approvals/displayed) ────────
    [Fact]
    public async Task WithoutARecentReportAnApprovalShowsAtOnce()
    {
        var h = new Harness();
        h.Model.Displayed([]); // T3 Code Desktop was visible…
        h.Clock.Advance(VaultApprovals.ReporterWindow); // …ten seconds ago
        _ = h.Ask("dev", "get"); await h.Pending(1);
        Assert.Equal(new VaultApprovalsVisibility(true, 1), h.Model.Sync());
    }

    [Fact]
    public async Task AnApprovalShownInT3StaysOutOfThePopOutUntilItsMarkExpires()
    {
        var h = new Harness();
        h.Model.Displayed([]); // visible, nothing to show yet
        _ = h.Ask("dev", "get"); await h.Pending(1);
        var id = h.Vault.PendingApprovals()[0].Id;
        Assert.Equal(new VaultApprovalsVisibility(false, 1), h.Model.Sync()); // its grace
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Model.Displayed([id]); // T3's next poll shows it inline
        Assert.True(h.Model.ShownElsewhere(id));
        for (var second = 1; second <= 60; second++) // renewed every two seconds: never shows
        {
            h.Clock.Advance(TimeSpan.FromSeconds(1));
            if (second % 2 == 0) h.Model.Displayed([id]);
            Assert.Equal(new VaultApprovalsVisibility(false, 1), h.Model.Sync());
        }
        // T3 Code Desktop was minimized: no more reports. Its mark ends eight seconds after the last one.
        h.Clock.Advance(VaultApprovals.DisplayedFor - TimeSpan.FromMilliseconds(1));
        Assert.False(h.Model.Sync().Visible);
        h.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(h.Model.ShownElsewhere(id));
        Assert.Equal(new VaultApprovalsVisibility(true, 1), h.Model.Sync());
        // Visible again and showing it: the pop-out steps back.
        h.Model.Displayed([id]);
        Assert.Equal(new VaultApprovalsVisibility(false, 1), h.Model.Sync());
    }

    [Fact]
    public async Task AnApprovalT3LeavesOutShowsAfterItsGraceListingAll()
    {
        var h = new Harness();
        var get = h.Ask("dev", "get"); await h.Pending(1);
        var shown = h.Vault.PendingApprovals()[0].Id;
        h.Model.Displayed([shown]);
        Assert.Equal(new VaultApprovalsVisibility(false, 1), h.Model.Sync());
        _ = h.Ask("build", "delete"); await h.Pending(2);
        var left = h.Vault.PendingApprovals()[1].Id;
        for (var at = TimeSpan.Zero; at < VaultApprovals.DisplayGrace; at += TimeSpan.FromSeconds(1))
        {
            h.Model.Displayed([shown]); // alive, but without the new one
            Assert.Equal(new VaultApprovalsVisibility(false, 2), h.Model.Sync());
            h.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        h.Model.Displayed([shown]);
        Assert.Equal(new VaultApprovalsVisibility(true, 2), h.Model.Sync());
        Assert.Equal([shown, left], h.Items().Select(i => i!["id"]!.GetValue<string>())); // every pending approval, the one T3 shows too
        // Answered in the pop-out: it vanishes, and the one T3 shows is no reason to stay.
        h.Clock.Advance(VaultApprovals.ArmDelay);
        Assert.Null(await h.Model.DecideAsync(Harness.Decide(left, "deny"), default));
        h.Model.Displayed([shown]);
        Assert.Equal(new VaultApprovalsVisibility(false, 1), h.Model.Sync());
        Assert.Equal([shown], h.Items().Select(i => i!["id"]!.GetValue<string>()));
        // Answered in T3: none is left.
        Assert.Equal(VaultDecision.Decided, await h.Vault.DecideAsync(shown, true, otherApp: true));
        Assert.Equal("ok", (await get)["status"]!.GetValue<string>());
        Assert.Equal(new VaultApprovalsVisibility(false, 0), h.Model.Sync());
        Assert.Empty(h.Items());
    }

    [Fact]
    public async Task TheTrayBringsThePopOutUpWhileT3ShowsTheApprovals()
    {
        var h = new Harness();
        h.Model.Displayed([]);
        _ = h.Ask("dev", "get"); await h.Pending(1);
        var id = h.Vault.PendingApprovals()[0].Id;
        h.Model.Displayed([id]);
        Assert.False(h.Model.Sync().Visible);
        Assert.True(h.Model.Open()); // Key vault request (1)… or a left click
        for (var second = 0; second < 20; second++)
        {
            h.Model.Displayed([id]);
            Assert.Equal(new VaultApprovalsVisibility(true, 1), h.Model.Sync()); // stays, although T3 shows it
            h.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(id, Assert.Single(h.Items())!["id"]!.GetValue<string>());
        h.Model.Dismiss();
        Assert.False(h.Model.Sync().Visible);
        h.Clock.Advance(VaultApprovals.ReporterWindow); // T3 went away, but the user closed the pop-out for this one
        Assert.Equal(new VaultApprovalsVisibility(false, 1), h.Model.Sync());
        Assert.True(h.Model.Open());
        Assert.True(h.Model.Sync().Visible);
    }

    [Fact]
    public async Task ReportsIgnoreUnknownAndDecidedApprovals()
    {
        var h = new Harness();
        var get = h.Ask("dev", "get"); await h.Pending(1);
        var id = h.Vault.PendingApprovals()[0].Id;
        Assert.Null(h.Model.ReportedAt);
        h.Model.Displayed(["0123456789abcdef0123456789abcdef"]);
        Assert.Equal(h.Clock.UtcNow, h.Model.ReportedAt); // any report counts as a visible T3
        Assert.False(h.Model.ShownElsewhere("0123456789abcdef0123456789abcdef"));
        Assert.Equal(VaultDecision.Decided, await h.Vault.DecideAsync(id, false));
        Assert.Equal("denied", (await get)["status"]!.GetValue<string>());
        h.Model.Displayed([id]); // a moment behind
        Assert.False(h.Model.ShownElsewhere(id));
        Assert.Equal(new VaultApprovalsVisibility(false, 0), h.Model.Sync());
    }

    [Fact]
    public void DisplayedReportsAreValidated()
    {
        static IReadOnlyList<string>? Ids(JsonNode? ids) => VaultApprovals.DisplayedIds(new JsonObject { ["ids"] = ids });
        static JsonArray List(int count) => new(Enumerable.Range(0, count).Select(i => (JsonNode)JsonValue.Create($"id-{i}")).ToArray());
        Assert.Empty(Ids(new JsonArray())!);
        Assert.Equal(["a.b~c-d_E9", new string('f', 128)], Ids(new JsonArray("a.b~c-d_E9", new string('f', 128))));
        Assert.Equal(VaultApprovals.MaxDisplayed, Ids(List(VaultApprovals.MaxDisplayed))!.Count);
        Assert.Null(VaultApprovals.DisplayedIds(new JsonObject()));
        foreach (var bad in new JsonNode?[] { null, "abc", new JsonObject(), List(VaultApprovals.MaxDisplayed + 1), new JsonArray(1), new JsonArray((JsonNode?)null), new JsonArray(""),
            new JsonArray("a/b"), new JsonArray("a b"), new JsonArray(new string('f', 129)), new JsonArray(new JsonArray("x")) })
            Assert.Null(Ids(bad));
        Assert.Null(VaultApprovals.DisplayedIds(JsonNode.Parse("""{"ids":["ok",2]}""")!.AsObject())); // parsed like the route's body
        Assert.Equal(["ok"], VaultApprovals.DisplayedIds(JsonNode.Parse("""{"ids":["ok"],"other":true}""")!.AsObject()));
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
