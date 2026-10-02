using System.Text;
using System.Text.Json.Nodes;
using Construct.Companion.Core;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Core.Vault;
using Construct.Companion.Fakes;
using static Construct.Companion.Tests.Runtime.ProcessSupervisorTests;
namespace Construct.Companion.Tests.Vault;

public sealed class VaultTests
{
    private const string Path = "/fake/local/The-Construct-Vault/vault.dat";
    private sealed class Harness
    {
        public FakeClock Clock { get; } = new();
        public FakeFileSystem Files { get; }
        public FakeDataProtection Protection { get; } = new();
        public FakePrompts Prompts { get; } = new();
        public FakeToastRaiser Toasts { get; } = new();
        public VaultService Vault { get; }
        // prompted: Prompts.Approvals / ApprovalHandler answer the pending approvals one at a time, as the tray
        // pop-out's buttons do; without it they wait for DecideAsync or their deadline.
        public Harness(Action<VaultService>? seed = null, bool prompted = true)
        {
            Clock.Advance(TimeSpan.FromDays(20000)); Files = new(Clock);
            Vault = new(new VaultStore(Files, Protection, Path), Prompts, Toasts, Clock);
            if (prompted) _ = new PromptApprover(Vault, Prompts);
            seed?.Invoke(Vault);
        }
        public VaultService Reopen() => new(new VaultStore(Files, Protection, Path), Prompts, Toasts, Clock);
        public Task<JsonObject> Ask(string instance, JsonObject request) => Vault.HandleAsync(instance, Parse(request));
        public IReadOnlyList<(string Instance, string Name, DateTimeOffset DueAt)> Pending => Vault.PendingCleanups();
    }
    private static int ids;
    private static JsonObject Req(string op, params string[] names) => new()
    {
        ["v"] = 1, ["id"] = $"1700000000000-{Interlocked.Increment(ref ids)}-42", ["op"] = op, ["names"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray()), ["source"] = "root@agent-vm"
    };
    private static VaultRequest Parse(JsonObject request)
    {
        var (parsed, _, error) = VaultProtocol.ParseRequest(request.ToJsonString());
        Assert.True(parsed is not null, error); return parsed!;
    }
    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    private static string Decode(JsonNode? node) => Encoding.UTF8.GetString(Convert.FromBase64String(node!.GetValue<string>()));
    private static void Seed(VaultService vault, string name = "github-token", string value = "ghp_s3cretTokenValue", string username = "")
        => vault.Save(new(name, "GitHub token for CI", username, new Secret(value)));

    [Fact]
    public void StoreRoundTripsProtectedAndNeverOverwritesAnUnreadableVault()
    {
        var h = new Harness(v => Seed(v));
        var stored = h.Files.ReadFile(Path)!;
        Assert.DoesNotContain("ghp_s3cret", Encoding.UTF8.GetString(stored));
        Assert.Equal("ghp_s3cretTokenValue", h.Reopen().Reveal("github-token")!.Reveal());

        h.Protection.Denied = true;
        var broken = h.Reopen();
        Assert.NotNull(broken.Unavailable);
        Assert.Throws<VaultUnavailableException>(() => broken.Save(new("other", "", "", new Secret("value123"))));
        Assert.Equal(stored, h.Files.ReadFile(Path));
        var moved = broken.ResetUnreadable();
        Assert.Equal(stored, h.Files.ReadFile(moved)); Assert.Null(broken.Unavailable); Assert.Empty(broken.Secrets());
    }

    [Fact]
    public async Task UnreadableVaultAnswersEveryRequestWithAnError()
    {
        var h = new Harness(v => Seed(v)); h.Protection.Denied = true;
        var response = await h.Reopen().HandleAsync("dev", Parse(Req("list")));
        Assert.Equal("error", response["status"]!.GetValue<string>());
        Assert.Contains("Key Vault", response["message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"v\":1,\"id\":\"x\",\"op\":\"list\"}", false, null)]
    [InlineData("not json", false, null)]
    [InlineData("{\"v\":1,\"id\":\"1700-1-2a\",\"op\":\"steal\"}", true, "Unknown operation.")]
    [InlineData("{\"v\":2,\"id\":\"1700-1-2a\",\"op\":\"list\"}", true, "Unsupported request version")]
    [InlineData("{\"v\":1,\"id\":\"1700-1-2a\",\"op\":\"get\",\"names\":[\"../etc\"]}", true, "Secret names use")]
    [InlineData("{\"v\":1,\"id\":\"1700-1-2a\",\"op\":\"get\",\"names\":[\"a\",\"b\"]}", true, "exactly one")]
    [InlineData("{\"v\":1,\"id\":\"1700-1-2a\",\"op\":\"request\",\"names\":[]}", true, "between 1 and 20")]
    [InlineData("{\"v\":1,\"id\":\"1700-1-2a\",\"op\":\"request\",\"names\":[\"a\"],\"uses\":0}", true, "--uses")]
    [InlineData("{\"v\":1,\"id\":\"1700-1-2a\",\"op\":\"request\",\"names\":[\"a\"],\"ttl\":90000}", true, "--for")]
    [InlineData("{\"v\":1,\"id\":\"1700-1-2a\",\"op\":\"release\",\"names\":[]}", true, "--all")]
    [InlineData("{\"v\":1,\"id\":\"1700-1-2a\",\"op\":\"add\",\"names\":[\"a\"],\"secret\":\"c2VjcmV0\"}", true, "--description")]
    [InlineData("{\"v\":1,\"id\":\"1700-1-2a\",\"op\":\"add\",\"names\":[\"a\"],\"description\":\"d\",\"secret\":\"%%%\"}", true, "base64")]
    [InlineData("{\"v\":1,\"id\":\"1700-1-2a\",\"op\":\"add\",\"names\":[\"a\"],\"description\":\"d\",\"secret\":\"\"}", true, "empty")]
    [InlineData("{\"v\":1,\"id\":\"1700-1-2a\",\"op\":\"add\",\"names\":[\"a\"],\"description\":\"d\",\"secret\":\"/w==\"}", true, "UTF-8")]
    public void MalformedRequestsAreDroppedOrAnsweredInvalid(string line, bool answerable, string? error)
    {
        var (request, id, message) = VaultProtocol.ParseRequest(line);
        Assert.Null(request);
        if (!answerable) { Assert.Null(id); return; }
        Assert.Equal("1700-1-2a", id); Assert.Contains(error!, message);
    }

    [Fact]
    public void RequestFieldsAreValidatedAndCleaned()
    {
        var line = new JsonObject
        {
            ["v"] = 1, ["id"] = "1700000000000-1-2", ["op"] = "add", ["names"] = new JsonArray("db", "db"), ["ttl"] = 600, ["replace"] = true,
            ["reason"] = "line\none", ["description"] = "Staging DB", ["username"] = "admin", ["secret"] = B64("pässword\n"), ["deadline"] = 1700000600000, ["source"] = "root@vm"
        }.ToJsonString();
        var (request, _, _) = VaultProtocol.ParseRequest(line);
        Assert.NotNull(request);
        Assert.Equal(["db"], request.Names); Assert.Equal(600, request.Ttl); Assert.True(request.Replace); Assert.Equal("line one", request.Reason);
        Assert.Equal("pässword\n", request.Value!.Reveal()); Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000600000), request.Deadline);
        Assert.DoesNotContain("pässword", request.ToString());
        var big = new JsonObject { ["v"] = 1, ["id"] = "1700000000000-1-2", ["op"] = "add", ["names"] = new JsonArray("a"), ["description"] = "d", ["secret"] = Convert.ToBase64String(new byte[32769]) };
        Assert.Contains("KiB", VaultProtocol.ParseRequest(big.ToJsonString()).Error);
    }

    [Fact]
    public void PatternsCoverEscapedFormsAndSkipShortValuesAndPemArmor()
    {
        var patterns = VaultProtocol.Patterns("p@ss\"wörd\\x", "deploy");
        Assert.Contains("p@ss\"wörd\\x", patterns);
        Assert.Contains("p@ss\\\"wörd\\\\x", patterns);
        Assert.Contains("p@ss\\\"w\\u00f6rd\\\\x", patterns);
        Assert.Contains(Uri.EscapeDataString("p@ss\"wörd\\x"), patterns);
        Assert.Contains(B64("deploy:p@ss\"wörd\\x"), patterns);
        Assert.Empty(VaultProtocol.Patterns("12345"));
        var pem = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW\nshort\n-----END OPENSSH PRIVATE KEY-----\n";
        var lines = VaultProtocol.Patterns(pem, "root");
        Assert.Contains("b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW", lines);
        Assert.DoesNotContain(lines, l => l.Contains("BEGIN", StringComparison.Ordinal) || l == "short" || l.Contains('\n', StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l == B64("root:" + pem));
    }

    [Theory]
    [InlineData("/root/.claude/projects/-root-repos-x/0b1.jsonl", true)]
    [InlineData("/home/dev/.claude/history.jsonl", true)]
    [InlineData("/root/.codex/sessions/2026/10/02/rollout.jsonl", true)]
    [InlineData("/root/.codex/logs_2.sqlite-wal", true)]
    [InlineData("/root/.local/share/opencode/opencode.db", true)]
    [InlineData("/root/.t3/userdata/state.sqlite", true)]
    [InlineData("/root/.t3/userdata/logs/terminals/a.log", true)]
    [InlineData("/tmp/claude-1a2b-cwd", true)]
    [InlineData("/root/.claude/settings.json", false)]
    [InlineData("/root/.claude.json", false)]
    [InlineData("/root/repos/app/.env", false)]
    [InlineData("/root/.bash_history", false)]
    [InlineData("/home/dev/projects/.claude/projects/x", false)]
    public void AgentLogsAreRecognizedByLocation(string path, bool agent) => Assert.Equal(agent, VaultProtocol.IsAgentLog(path));

    [Fact]
    public void ScanAndCleanOutputParseStrictly()
    {
        var path = VaultProtocol.EncodePath("/root/a b.txt");
        var (hits, complete) = VaultProtocol.ParseScan($"F\t0,2\t12\ttext\t{path}\nF\tx\t1\ttext\t{path}\nF\t1\t3\tweird\t{VaultProtocol.EncodePath("relative")}\ngarbage\nDONE\t1\n");
        Assert.True(complete); var hit = Assert.Single(hits);
        Assert.Equal("/root/a b.txt", hit.Path); Assert.Equal([0, 2], hit.Indexes); Assert.Equal(12, hit.Size);
        Assert.False(VaultProtocol.ParseScan($"F\t0\t1\ttext\t{path}\n").Complete);
        var (results, done) = VaultProtocol.ParseClean($"R\tok\t{path}\t3\nR\tfailed\t{path}\t0\tpython3 missing\nDONE\n");
        Assert.True(done); Assert.Equal(["ok", "failed"], results.Select(r => r.Status)); Assert.Equal("python3 missing", results[1].Detail);
        Assert.Equal("/root/.t3/userdata/state.sqlite", VaultProtocol.DatabaseFor("/root/.t3/userdata/state.sqlite-wal"));
    }

    [Fact]
    public async Task ListShowsDescriptionsAndThisVmsLeasesButNeverValues()
    {
        var h = new Harness(v => { Seed(v); v.Save(new("db-admin", "Staging DB", "admin", new Secret("Adm1nPassw0rd"))); });
        var list = await h.Ask("dev", Req("list"));
        Assert.Equal("ok", list["status"]!.GetValue<string>());
        Assert.DoesNotContain("ghp_s3cret", list.ToJsonString()); Assert.DoesNotContain("Adm1n", list.ToJsonString());
        var items = list["items"]!.AsArray();
        Assert.Equal(["db-admin", "github-token"], items.Select(i => i!["name"]!.GetValue<string>()));
        Assert.True(items[0]!["hasUsername"]!.GetValue<bool>()); Assert.Null(items[0]!["lease"]);
        Assert.Empty((await h.Ask("dev", Req("status")))["items"]!.AsArray());
    }

    [Fact]
    public async Task GetWithoutLeaseAsksOnceAndScrubsAfterTheSingleUse()
    {
        var h = new Harness(v => Seed(v, username: "ci-bot"));
        h.Prompts.Approvals.Enqueue(true);
        var get = await h.Ask("dev", Req("get", "github-token"));
        Assert.Equal("ok", get["status"]!.GetValue<string>());
        Assert.Equal("ghp_s3cretTokenValue", Decode(get["secret"])); Assert.Equal("ci-bot", get["username"]!.GetValue<string>());
        Assert.Equal(0, get["lease"]!["usesLeft"]!.GetValue<int>());
        var prompt = Assert.IsType<ApprovalPrompt>(Assert.Single(h.Prompts.Shown));
        Assert.Contains("dev", prompt.Message); Assert.Contains("github-token", prompt.Message); Assert.DoesNotContain("ghp_", prompt.Message);
        var pending = Assert.Single(h.Pending);
        Assert.Equal(("dev", "github-token"), (pending.Instance, pending.Name)); Assert.Equal(h.Clock.UtcNow + VaultService.ExhaustedGrace, pending.DueAt);
        h.Prompts.Approvals.Enqueue(false);
        var again = await h.Ask("dev", Req("get", "github-token"));
        Assert.Equal("denied", again["status"]!.GetValue<string>()); Assert.Null(again["secret"]);
    }

    [Fact]
    public async Task PreApprovedUsesAreConsumedWithoutAskingAgain()
    {
        var h = new Harness(v => { Seed(v); v.Save(new("npm", "npm publish token", "", new Secret("npm_abcdefgh"))); });
        h.Prompts.Approvals.Enqueue(true);
        var request = Req("request", "github-token", "npm"); request["uses"] = 2; request["reason"] = "release";
        var granted = await h.Ask("dev", request);
        Assert.Equal(["github-token", "npm"], granted["names"]!.AsArray().Select(n => n!.GetValue<string>()));
        var prompt = Assert.IsType<ApprovalPrompt>(Assert.Single(h.Prompts.Shown));
        Assert.Contains("2 uses", prompt.Message); Assert.Contains("“release”", prompt.Message); Assert.Contains("npm publish token", prompt.Message);
        Assert.Equal(1, (await h.Ask("dev", Req("get", "npm")))["lease"]!["usesLeft"]!.GetValue<int>());
        Assert.Equal(0, (await h.Ask("dev", Req("get", "npm")))["lease"]!["usesLeft"]!.GetValue<int>());
        Assert.Single(h.Prompts.Shown);
        var status = await h.Ask("dev", Req("status"));
        Assert.Equal(["github-token"], status["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()));
        // Another VM holds nothing and is asked separately.
        h.Prompts.Approvals.Enqueue(false);
        Assert.Equal("denied", (await h.Ask("other", Req("get", "github-token")))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task TimedLeaseExpiresIntoAScrubAndUnknownNamesAreNotPrompted()
    {
        var h = new Harness(v => Seed(v));
        Assert.Equal("notFound", (await h.Ask("dev", Req("request", "github-token", "nope")))["status"]!.GetValue<string>());
        Assert.Empty(h.Prompts.Shown);
        h.Prompts.Approvals.Enqueue(true);
        var request = Req("request", "github-token"); request["ttl"] = 600;
        var lease = (await h.Ask("dev", request))["lease"]!;
        Assert.Null(lease["usesLeft"]); Assert.Equal((h.Clock.UtcNow + TimeSpan.FromMinutes(10)).ToUnixTimeMilliseconds(), lease["expiresAt"]!.GetValue<long>());
        for (var i = 0; i < 3; i++) Assert.Equal("ok", (await h.Ask("dev", Req("get", "github-token")))["status"]!.GetValue<string>());
        h.Clock.Advance(TimeSpan.FromMinutes(10)); h.Vault.Sweep();
        Assert.Empty(h.Vault.Leases()); Assert.Single(h.Pending);
        Assert.Contains(h.Vault.Activity(), a => a.Text.Contains("expired", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReleaseEndsLeasesAndSchedulesTheScrub()
    {
        var h = new Harness(v => { Seed(v); v.Save(new("npm", "npm", "", new Secret("npm_abcdefgh"))); });
        h.Prompts.Approvals.Enqueue(true);
        await h.Ask("dev", Req("request", "github-token", "npm"));
        var released = await h.Ask("dev", Req("release", "npm"));
        Assert.Equal(["npm"], released["names"]!.AsArray().Select(n => n!.GetValue<string>()));
        var all = Req("release"); all["all"] = true;
        Assert.Equal(["github-token"], (await h.Ask("dev", all))["names"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("Nothing to release.", (await h.Ask("dev", all))["message"]!.GetValue<string>());
        Assert.Equal(2, h.Pending.Count); Assert.All(h.Pending, p => Assert.Equal(h.Clock.UtcNow + VaultService.ReleaseDelay, p.DueAt));
    }

    [Fact]
    public async Task AgentsAddWithoutApprovalButReplaceAndDeleteNeedTheUser()
    {
        var h = new Harness();
        var add = Req("add", "deploy-key"); add["description"] = "Deploy key for prod"; add["username"] = "deployer"; add["secret"] = B64("first-value-123");
        var stored = await h.Ask("dev", add);
        Assert.Equal("ok", stored["status"]!.GetValue<string>()); Assert.Null(stored["lease"]!["usesLeft"]);
        Assert.Empty(h.Prompts.Shown); Assert.Contains("deploy-key", Assert.Single(h.Toasts.Toasts).Xml);
        Assert.Equal("agent:dev", h.Vault.Secrets().Single().Origin); Assert.Equal("dev", h.Vault.Leases().Single().Instance);
        Assert.Equal("first-value-123", Decode((await h.Ask("dev", Req("get", "deploy-key")))["secret"]));
        add["id"] = "1700000000000-9000-1";
        Assert.Equal("exists", (await h.Ask("dev", add))["status"]!.GetValue<string>());

        h.Prompts.Approvals.Enqueue(true);
        var replace = Req("add", "deploy-key"); replace["description"] = "Rotated"; replace["secret"] = B64("second-value-456"); replace["replace"] = true;
        Assert.Equal("ok", (await h.Ask("dev", replace))["status"]!.GetValue<string>());
        Assert.Equal("second-value-456", h.Vault.Reveal("deploy-key")!.Reveal());
        Assert.Single(h.Pending); // the old value is scrubbed from the VM that held it

        h.Prompts.Approvals.Enqueue(false);
        Assert.Equal("denied", (await h.Ask("dev", Req("delete", "deploy-key")))["status"]!.GetValue<string>());
        h.Prompts.Approvals.Enqueue(true);
        Assert.Equal("ok", (await h.Ask("dev", Req("delete", "deploy-key")))["status"]!.GetValue<string>());
        Assert.Empty(h.Vault.Secrets()); Assert.Empty(h.Vault.Leases()); Assert.Equal(2, h.Pending.Count);
        Assert.Equal("notFound", (await h.Ask("dev", Req("delete", "deploy-key")))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task RequestsWaitSideBySideAndAnUnansweredOneEndsAtTheAgentsDeadline()
    {
        var h = new Harness(v => Seed(v), prompted: false); var changes = 0; h.Vault.ApprovalsChanged += () => Interlocked.Increment(ref changes);
        var first = Req("get", "github-token"); first["deadline"] = (h.Clock.UtcNow + TimeSpan.FromMinutes(5)).ToUnixTimeMilliseconds();
        var a = h.Ask("dev", first); var b = h.Ask("other", Req("request", "github-token"));
        await Eventually(() => h.Vault.PendingApprovals().Count == 2 && h.Clock.PendingDelays > 0);
        Assert.Equal(["dev", "other"], h.Vault.PendingApprovals().Select(p => p.Instance)); Assert.Equal(2, changes);
        Assert.Null(h.Vault.PendingApprovals()[1].Deadline); // an older CLI sends none
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        var timedOut = await a;
        Assert.Equal("denied", timedOut["status"]!.GetValue<string>()); Assert.Contains("timed out", timedOut["message"]!.GetValue<string>());
        Assert.Equal("other", Assert.Single(h.Vault.PendingApprovals()).Instance); Assert.Equal(3, changes);
        Assert.Equal(VaultDecision.Decided, await h.Vault.DecideAsync(h.Vault.PendingApprovals()[0].Id, true));
        Assert.Equal("ok", (await b)["status"]!.GetValue<string>());
        Assert.Empty(h.Vault.PendingApprovals()); Assert.Equal(4, changes); Assert.Empty(h.Prompts.Shown);
    }

    // A fake dialog (the tray pop-out's stand-in) that stays open until its record ends elsewhere.
    private sealed class OpenDialogs
    {
        public int Shown, Closed;
        public Func<ApprovalPrompt, CancellationToken, Task<bool>> Handler => async (_, token) =>
        {
            Interlocked.Increment(ref Shown);
            try { await Task.Delay(Timeout.Infinite, token); return false; }
            finally { Interlocked.Increment(ref Closed); }
        };
    }

    [Fact]
    public async Task APendingRequestIsListedAndAnotherAppsAnswerEndsIt()
    {
        var h = new Harness(v => Seed(v, username: "ci-bot")); var dialogs = new OpenDialogs(); h.Prompts.ApprovalHandler = dialogs.Handler;
        var request = Req("get", "github-token"); request["reason"] = "deploy"; request["deadline"] = (h.Clock.UtcNow + TimeSpan.FromMinutes(5)).ToUnixTimeMilliseconds();
        var get = h.Ask("dev", request);
        await Eventually(() => dialogs.Shown == 1);
        var listed = Assert.Single(h.Vault.PendingApprovals());
        var prompt = Assert.IsType<ApprovalPrompt>(Assert.Single(h.Prompts.Shown));
        Assert.Equal(("dev", "dev", "local", null, request["id"]!.GetValue<string>(), null, "get"), (listed.Instance, listed.Vm, listed.Kind, listed.Host, listed.RequestId, listed.HostRequestId, listed.Op));
        Assert.Equal((prompt.Title, prompt.Message, prompt.Action, prompt.Deny), (listed.Title, listed.Message, listed.Action, listed.Deny));
        Assert.Equal(["github-token"], listed.Names); Assert.Equal(h.Clock.UtcNow, listed.CreatedAt); Assert.Equal(h.Clock.UtcNow + TimeSpan.FromMinutes(5), listed.Deadline);
        Assert.Matches("^[A-Za-z0-9._~-]{1,128}$", listed.Id);
        Assert.DoesNotContain("ghp_s3cret", listed.ToString()); Assert.DoesNotContain("ci-bot", listed.ToString());

        Assert.Equal(VaultDecision.Decided, await h.Vault.DecideAsync(listed.Id, true, otherApp: true));
        var answer = await get;
        Assert.Equal("ok", answer["status"]!.GetValue<string>()); Assert.Equal("ghp_s3cretTokenValue", Decode(answer["secret"]));
        await Eventually(() => dialogs.Closed == 1); Assert.Empty(h.Vault.PendingApprovals());
        Assert.Contains(h.Vault.Activity(), a => a.Text == "Approved access to github-token from another app on this PC.");
        // The first answer counts; an unknown id was never pending.
        Assert.Equal(VaultDecision.AlreadyDecided, await h.Vault.DecideAsync(listed.Id, false));
        Assert.Equal(VaultDecision.NotFound, await h.Vault.DecideAsync("no-such-approval", true));

        // A denial ends the request the same way.
        var delete = h.Ask("dev", Req("delete", "github-token"));
        await Eventually(() => dialogs.Shown == 2);
        Assert.Equal(VaultDecision.Decided, await h.Vault.DecideAsync(Assert.Single(h.Vault.PendingApprovals()).Id, false));
        Assert.Equal("denied", (await delete)["status"]!.GetValue<string>());
        Assert.NotNull(h.Vault.Reveal("github-token"));
        Assert.DoesNotContain(h.Vault.Activity(), a => a.Text.StartsWith("Denied", StringComparison.Ordinal)); // the tray pop-out's answers are not "another app"
    }

    [Fact]
    public async Task TheFirstAnswerOrTheDeadlineWinsOverALateDecision()
    {
        var h = new Harness(v => Seed(v));
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Prompts.ApprovalHandler = (_, _) => answer.Task;
        var get = h.Ask("dev", Req("get", "github-token"));
        await Eventually(() => h.Vault.PendingApprovals().Count == 1);
        var id = h.Vault.PendingApprovals()[0].Id;
        answer.SetResult(false);
        Assert.Equal("denied", (await get)["status"]!.GetValue<string>());
        Assert.Equal(VaultDecision.AlreadyDecided, await h.Vault.DecideAsync(id, true));
        Assert.Empty(h.Vault.Leases());

        var dialogs = new OpenDialogs(); h.Prompts.ApprovalHandler = dialogs.Handler;
        var late = Req("get", "github-token"); late["deadline"] = (h.Clock.UtcNow + TimeSpan.FromMinutes(1)).ToUnixTimeMilliseconds();
        var expiring = h.Ask("dev", late);
        await Eventually(() => dialogs.Shown == 1 && h.Clock.PendingDelays > 0);
        id = h.Vault.PendingApprovals()[0].Id;
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Contains("timed out", (await expiring)["message"]!.GetValue<string>());
        Assert.Equal(VaultDecision.NotFound, await h.Vault.DecideAsync(id, true));
    }

    [Fact]
    public async Task WindowEditsValidateAndANewValueEndsLeasesOnTheOldOne()
    {
        var h = new Harness(v => Seed(v));
        Assert.Throws<ArgumentException>(() => h.Vault.Save(new("bad name", "", "", new Secret("x1234567"))));
        Assert.Throws<ArgumentException>(() => h.Vault.Save(new("new-one", "", "", null)));
        h.Vault.Save(new("other", "", "", new Secret("other-value")));
        Assert.Throws<ArgumentException>(() => h.Vault.Save(new("other", "", "", null), "github-token"));
        h.Vault.Save(new("github-token", "Renamed description", "", null), "github-token");
        Assert.Equal("ghp_s3cretTokenValue", h.Vault.Reveal("github-token")!.Reveal());
        Assert.Equal("Renamed description", h.Vault.Secrets().Single(s => s.Name == "github-token").Description);
        h.Prompts.Approvals.Enqueue(true);
        await h.Ask("dev", Req("request", "github-token"));
        h.Vault.Save(new("github-token", "Renamed description", "", null), "github-token");
        Assert.Single(h.Vault.Leases()); Assert.Empty(h.Pending); // metadata edits keep access
        h.Vault.Save(new("github-token", "Rotated", "", new Secret("ghp_rotatedValue")), "github-token");
        Assert.Empty(h.Vault.Leases()); Assert.Equal("ghp_s3cretTokenValue", h.Vault.PendingCleanups().Count == 1 ? "ghp_s3cretTokenValue" : "");
        Assert.Equal("ghp_rotatedValue", h.Vault.Reveal("github-token")!.Reveal());
    }

    [Fact]
    public async Task ScrubRedactsAgentLogsAndAsksAboutOtherFiles()
    {
        var h = new Harness(v => Seed(v, username: "ci-bot"));
        h.Prompts.Approvals.Enqueue(true);
        await h.Ask("dev", Req("get", "github-token"));
        var transcript = VaultProtocol.EncodePath("/root/.claude/projects/-root-repos-app/abc.jsonl");
        var wal = VaultProtocol.EncodePath("/root/.t3/userdata/state.sqlite-wal");
        var env = VaultProtocol.EncodePath("/root/repos/app/.env");
        var notes = VaultProtocol.EncodePath("/root/notes.txt");
        var ssh = new FakeSshTransport(); var cleans = new List<string>(); string? scanInput = null;
        ssh.ScriptHandler = (script, _) =>
        {
            var stdin = ssh.StandardInputs[^1]!.Reveal();
            if (script == VaultProtocol.ScanScript()) { scanInput = stdin; return Task.FromResult(new ProcessResult(0, $"F\t0\t120\ttext\t{transcript}\nF\t0\t4096\tsqlite-aux\t{wal}\nF\t0\t30\ttext\t{env}\nF\t0\t10\ttext\t{notes}\nDONE\t4\n")); }
            Assert.Equal(VaultProtocol.CleanScript(), script); cleans.Add(stdin);
            var output = string.Join("", stdin.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => $"R\tok\t{l.Split('\t')[1]}\t1\n")) + "DONE\n";
            return Task.FromResult(new ProcessResult(0, output));
        };
        h.Prompts.FileDecisions.Enqueue(new Dictionary<string, string> { [env] = "redact", [notes] = "keep" });
        using (h.Vault.Attach("dev", ssh))
        {
            h.Vault.Sweep(); await h.Vault.DrainAsync(); Assert.Null(scanInput); // not due yet: the last use gets a grace minute
            h.Clock.Advance(VaultService.ExhaustedGrace); h.Vault.Sweep(); await h.Vault.DrainAsync();
        }
        Assert.Contains("0\t" + B64("ghp_s3cretTokenValue"), scanInput); Assert.Contains("0\t" + B64(B64("ci-bot:ghp_s3cretTokenValue")), scanInput);
        Assert.DoesNotContain("ghp_s3cret", scanInput);
        Assert.Equal(2, cleans.Count);
        Assert.Contains("redact\t" + transcript + "\t", cleans[0]); Assert.Contains("redact\t" + VaultProtocol.EncodePath("/root/.t3/userdata/state.sqlite") + "\t", cleans[0]);
        Assert.DoesNotContain(env, cleans[0]); Assert.DoesNotContain(wal, cleans[0]);
        Assert.Equal($"redact\t{env}", cleans[1].Split('\t').Take(2).Aggregate((x, y) => x + "\t" + y));
        Assert.DoesNotContain(notes, cleans[1]);
        var decision = Assert.IsType<FileDecisionPrompt>(h.Prompts.Shown[^1]);
        Assert.Equal(["/root/notes.txt", "/root/repos/app/.env"], decision.Files.Select(f => f.Path));
        Assert.Contains("github-token", decision.Files[0].Detail);
        Assert.Empty(h.Pending);
        Assert.Contains("2 files of agent logs", Assert.Single(h.Toasts.Toasts).Xml);
        Assert.Contains(h.Vault.Activity(), a => a.Text.Contains("1 redacted, 0 deleted, 1 kept", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScrubWaitsForTheVmRetriesFailuresAndSkipsSecretsLeasedAgain()
    {
        var h = new Harness(v => Seed(v));
        h.Prompts.Approvals.Enqueue(true);
        await h.Ask("dev", Req("request", "github-token"));
        await h.Ask("dev", Req("release", "github-token"));
        h.Clock.Advance(VaultService.ReleaseDelay); h.Vault.Sweep(); await h.Vault.DrainAsync();
        Assert.Single(h.Pending); // nothing attached yet

        var fails = new FakeSshTransport { ScriptHandler = (_, _) => Task.FromResult(new ProcessResult(255, "", "Connection reset")) };
        using (h.Vault.Attach("dev", fails)) { h.Vault.Sweep(); await h.Vault.DrainAsync(); }
        Assert.Equal(h.Clock.UtcNow + VaultService.RetryDelay, Assert.Single(h.Pending).DueAt);
        Assert.Contains(h.Vault.Activity(), a => a.Warning && a.Text.Contains("retrying", StringComparison.Ordinal));

        h.Clock.Advance(VaultService.RetryDelay);
        h.Prompts.Approvals.Enqueue(true);
        await h.Ask("dev", Req("request", "github-token"));
        var scans = 0; var ssh = new FakeSshTransport { ScriptHandler = (_, _) => { scans++; return Task.FromResult(new ProcessResult(0, "DONE\t0\n")); } };
        using (h.Vault.Attach("dev", ssh)) { h.Vault.Sweep(); await h.Vault.DrainAsync(); Assert.Equal(0, scans); }
        await h.Ask("dev", Req("release", "github-token"));
        h.Clock.Advance(VaultService.ReleaseDelay);
        using (h.Vault.Attach("dev", ssh)) { h.Vault.Sweep(); await h.Vault.DrainAsync(); }
        Assert.Equal(1, scans); Assert.Empty(h.Pending);
        Assert.Contains(h.Vault.Activity(), a => a.Text.Contains("no copies", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TooShortSecretsAreReportedInsteadOfScanned()
    {
        var h = new Harness(v => v.Save(new("pin", "Phone PIN", "", new Secret("1234"))));
        h.Prompts.Approvals.Enqueue(true);
        await h.Ask("dev", Req("get", "pin"));
        h.Clock.Advance(VaultService.ExhaustedGrace);
        var ssh = new FakeSshTransport { ScriptHandler = (_, _) => throw new InvalidOperationException("must not scan") };
        using (h.Vault.Attach("dev", ssh)) { h.Vault.Sweep(); await h.Vault.DrainAsync(); }
        Assert.Empty(ssh.Scripts); Assert.Empty(h.Pending);
        Assert.Contains(h.Vault.Activity(), a => a.Warning && a.Text.Contains("too short", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BrokerAnswersClaimedRequestsThroughTheRespondScript()
    {
        var h = new Harness(v => Seed(v));
        var ssh = new FakeSshTransport { ScriptHandler = (_, _) => Task.FromResult(new ProcessResult(0)) };
        var processes = new FakeRuntimeProcesses();
        var broker = new VaultBroker("dev", ssh, processes, h.Vault);
        broker.Start(); broker.Start();
        var session = Assert.Single(processes.Sessions);
        var list = Req("list");
        await session.EmitAsync("#\n" + list.ToJsonString()[..20]); await session.EmitAsync(list.ToJsonString()[20..] + "\nnot json\n{\"v\":1,\"id\":\"1700-1-2a\",\"op\":\"steal\"}\n");
        await Eventually(() => ssh.Scripts.Count == 2);
        var answers = ssh.StandardInputs.Select(s => JsonNode.Parse(s!.Reveal())!.AsObject()).ToDictionary(a => a["id"]!.GetValue<string>());
        Assert.Equal("ok", answers[list["id"]!.GetValue<string>()]["status"]!.GetValue<string>());
        Assert.Equal("invalid", answers["1700-1-2a"]["status"]!.GetValue<string>());
        Assert.Contains(VaultProtocol.RespondScript("1700-1-2a"), ssh.Scripts);
        await broker.DisposeAsync(); Assert.True(session.Disposed);
    }

    [Fact]
    public void VaultIsReachableFromTrayCommandLineAndUri()
    {
        Assert.Contains(TrayModel.Menu(new("dev", true), ["dev"], [], true, false), m => m.Id == "vault");
        Assert.Equal("vault", Assert.Single(Activation.Resolve(CommandLine.Parse(["--vault"]), ["dev"], []).Views).View);
        Assert.Equal("vault", Assert.Single(Activation.Resolve(new CommandLine(Uri: "construct://vault"), ["dev"], []).Views).View);
        Assert.Equal("vault", Assert.Single(Activation.ResolveView(new("vault"), ["dev"], []).Views).View);
    }

    [Fact]
    public void GuestScriptsRenderWithoutLeftoverPlaceholders()
    {
        Assert.Equal(["vault-clean", "vault-respond", "vault-scan", "vault-watch"], VaultProtocol.ScriptNames.Order(StringComparer.Ordinal));
        foreach (var script in new[] { VaultProtocol.WatchScript(), VaultProtocol.RespondScript("1700000000000-1-2"), VaultProtocol.ScanScript(["/root", "/it's"]), VaultProtocol.CleanScript() })
            Assert.DoesNotMatch(@"\{\{[A-Za-z]", script);
        Assert.Contains("'/it'\\''s'", VaultProtocol.ScanScript(["/root", "/it's"]));
        Assert.Throws<ArgumentException>(() => VaultProtocol.RespondScript("../../x"));
    }
}
