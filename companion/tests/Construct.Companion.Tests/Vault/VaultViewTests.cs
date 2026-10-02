using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Vault;
using Construct.Companion.Fakes;
using Harness = Construct.Companion.Tests.Vault.VaultHostsTests.Harness;
namespace Construct.Companion.Tests.Vault;

// The Key Vault page's decisions (VaultView): what the page may see, and which request reaches which
// service call. The native side (editor, clipboard, pairing code) is FakeVaultWindow.
public sealed class VaultViewTests
{
    private const string Slug = VaultHostsTests.Slug, Value = "SENTINEL-value-9f3a1c", AgentValue = "SENTINEL-agent-77d0e2";
    private static int ids;
    private static (VaultView View, FakeVaultWindow Window) View(Harness h) { var window = new FakeVaultWindow(); return (new(h.Vault, h.Hosts, h.Prompts, window, h.Clock), window); }
    // What `construct secret add` sends from a VM: the VM holds the new secret for an hour.
    private static VaultRequest Add(string name, string value, string username = "")
    {
        var raw = new JsonObject
        {
            ["v"] = 1, ["id"] = $"1700000000000-{Interlocked.Increment(ref ids)}-7", ["op"] = "add", ["names"] = new JsonArray(name), ["description"] = "Added by an agent",
            ["username"] = username, ["secret"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)), ["source"] = "root@dev"
        };
        var (request, _, error) = VaultProtocol.ParseRequest(raw.ToJsonString());
        Assert.True(request is not null, error); return request!;
    }
    // Every string of the page's state, property names included, unescaped: a sentinel must appear in none.
    private static string Text(JsonNode? node) => node switch
    {
        JsonObject o => string.Join("\n", o.Select(p => p.Key + "\n" + Text(p.Value))),
        JsonArray a => string.Join("\n", a.Select(Text)),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => ""
    };
    private static JsonObject Element(JsonArray? list, string key, string value) => list!.OfType<JsonObject>().Single(o => o[key]!.GetValue<string>() == value);
    private static VaultCommand Parse(string json) { using var document = JsonDocument.Parse(json); return VaultView.Parse(document.RootElement); }

    [Fact]
    public async Task TheStateNeverCarriesAValueTheVaultKeyADeviceTokenOrAPairingLink()
    {
        await using var h = new Harness();
        var (view, window) = View(h);
        h.Vault.Save(new("github-token", "GitHub token for CI", "ci-bot", new Secret(Value)));
        Assert.Equal("ok", (await h.Vault.HandleAsync("dev", Add("deploy-key", AgentValue, "deployer")))["status"]!.GetValue<string>());
        h.Vault.Revoke(h.Vault.Leases().Single().Id); // the pending scrub carries the value it looks for
        await h.Vault.HandleAsync("dev", Add("npm-token", AgentValue + "-2"));
        await h.Sync(); // creates the vault key K and the host's key check
        h.Host.Edit(x =>
        {
            x.Leases.Add(new JsonObject { ["id"] = "l1", ["vm"] = "build-vm", ["name"] = "github-token", ["usesLeft"] = 2, ["expiresAt"] = h.Now + 3600000, ["reason"] = "release 4.2", ["origin"] = "approved" });
            x.Devices.Add(new JsonObject { ["id"] = "d9", ["label"] = "Old phone", ["createdAt"] = h.Now - 86400000, ["lastUsedAt"] = null });
        });
        await h.Hosts.RefreshAsync(default);
        h.Prompts.Picks.Enqueue(["build"]); h.Prompts.Inputs.Enqueue("Pixel");
        Assert.Null(await view.ExecuteAsync(new("pairPhone", Host: Slug), default));
        var pairing = Assert.Single(window.Pairings);

        var message = view.StateMessage();
        Assert.Equal("vault.state", message["type"]!.GetValue<string>());
        var text = Text(message);
        foreach (var sentinel in new[] { Value, AgentValue, VaultCrypto.Export(h.Key), h.State.KeyCheck!, new string('T', 20), pairing.Url.Reveal(), "t3-once", "vault/pair" })
            Assert.DoesNotContain(sentinel, text);
        var state = message["state"]!.AsObject();
        Assert.Equal(["activity", "hasKey", "hosts", "leases", "now", "scrubs", "secrets", "unavailable"], state.Select(p => p.Key).Order());
        var github = Element(state["secrets"]!.AsArray(), "name", "github-token");
        Assert.Equal(("GitHub token for CI", "ci-bot", "", 1), (github["description"]!.GetValue<string>(), github["username"]!.GetValue<string>(), github["addedBy"]!.GetValue<string>(), github["holders"]!.GetValue<int>()));
        var agent = Element(state["secrets"]!.AsArray(), "name", "npm-token");
        Assert.Equal(("dev", 1), (agent["addedBy"]!.GetValue<string>(), agent["holders"]!.GetValue<int>()));
        Assert.Equal(["build-vm", "dev"], state["leases"]!.AsArray().Select(l => l!["vm"]!.GetValue<string>()).Order());
        var hosted = Element(state["leases"]!.AsArray(), "id", "l1");
        Assert.Equal((Slug, "host.example", 2, "release 4.2"), (hosted["host"]!.GetValue<string>(), hosted["hostName"]!.GetValue<string>(), hosted["usesLeft"]!.GetValue<int>(), hosted["reason"]!.GetValue<string>()));
        var local = state["leases"]!.AsArray().OfType<JsonObject>().Single(l => l["host"]!.GetValue<string>() == "");
        Assert.Equal(("dev", "npm-token", "added"), (local["vm"]!.GetValue<string>(), local["name"]!.GetValue<string>(), local["origin"]!.GetValue<string>()));
        Assert.Null(local["usesLeft"]);
        var scrub = Assert.Single(state["scrubs"]!.AsArray())!;
        Assert.Equal(("dev", "deploy-key"), (scrub["vm"]!.GetValue<string>(), Assert.Single(scrub["names"]!.AsArray())!.GetValue<string>()));
        var host = Assert.Single(state["hosts"]!.AsArray())!.AsObject();
        Assert.Equal(["createdAt", "id", "label", "lastUsedAt"], host["devices"]!.AsArray()[0]!.AsObject().Select(p => p.Key).Order());
        Assert.Equal(["Old phone", "Pixel"], host["devices"]!.AsArray().Select(d => d!["label"]!.GetValue<string>()).Order());
        Assert.Equal(("In sync", "ok", VaultSync.Available), (host["status"]!.GetValue<string>(), host["level"]!.GetValue<string>(), host["mode"]!.GetValue<string>()));
        Assert.Equal(["build", "dev"], host["instances"]!.AsArray().Select(i => i!.GetValue<string>()).Order());
        Assert.True(state["hasKey"]!.GetValue<bool>());
        Assert.Contains(state["activity"]!.AsArray(), a => a!["text"]!.GetValue<string>().Contains("Revoked access to deploy-key", StringComparison.Ordinal));
        // Newest first.
        var times = state["activity"]!.AsArray().Select(a => a!["at"]!.GetValue<long>()).ToArray();
        Assert.Equal(times.OrderDescending(), times);
    }

    [Theory]
    [InlineData("""{"type":"vault.ready"}""", "ready", "", "", "", "", "")]
    [InlineData("""{"type":"vault.copySecret","name":"github-token"}""", "copySecret", "github-token", "", "", "", "")]
    [InlineData("""{"type":"vault.revokeLease","id":"0f1e"}""", "revokeLease", "", "", "0f1e", "", "")]
    [InlineData("""{"type":"vault.revokeLease","id":"l1","host":"host.example_7462"}""", "revokeLease", "", "host.example_7462", "l1", "", "")]
    [InlineData("""{"type":"vault.discardScrubs","vm":"work vm"}""", "discardScrubs", "", "", "", "", "work vm")]
    [InlineData("""{"type":"vault.setMode","host":"h_7462","mode":"locked"}""", "setMode", "", "h_7462", "", "locked", "")]
    [InlineData("""{"type":"vault.revokeDevice","host":"h_7462","id":"d9","extra":true}""", "revokeDevice", "", "h_7462", "d9", "", "")]
    public void PageRequestsParseIntoCommands(string json, string action, string name, string host, string id, string mode, string vm) =>
        Assert.Equal(new VaultCommand(action, name, host, id, mode, vm), Parse(json));

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"type":1}""")]
    [InlineData("""{"id":"openVault"}""")]
    [InlineData("""{"type":"command","id":"openVault"}""")]
    [InlineData("""{"type":"ready"}""")]
    [InlineData("""{"type":"vault."}""")]
    [InlineData("""{"type":"vault.reveal","name":"github-token"}""")]
    [InlineData("""{"type":"vault.copySecret"}""")]
    [InlineData("""{"type":"vault.copySecret","name":null}""")]
    [InlineData("""{"type":"vault.copySecret","name":5}""")]
    [InlineData("""{"type":"vault.edit","name":"../etc/passwd"}""")]
    [InlineData("""{"type":"vault.delete","name":""}""")]
    [InlineData("""{"type":"vault.revokeLease","id":"a/b"}""")]
    [InlineData("""{"type":"vault.revokeLease","id":""}""")]
    [InlineData("""{"type":"vault.discardScrubs","vm":"dev\nrm"}""")]
    [InlineData("""{"type":"vault.setMode","host":"h_7462","mode":"sometimes"}""")]
    [InlineData("""{"type":"vault.setMode","mode":"locked"}""")]
    [InlineData("""{"type":"vault.pairPhone","host":""}""")]
    [InlineData("""{"type":"vault.revokeDevice","host":"h_7462"}""")]
    public void MalformedOrUnknownRequestsAreRefused(string json) => Assert.Throws<ArgumentException>(() => Parse(json));

    [Fact]
    public async Task AnUnknownCommandIsRefusedBeforeAnythingRuns()
    {
        await using var h = new Harness();
        var (view, window) = View(h);
        await Assert.ThrowsAsync<ArgumentException>(() => view.ExecuteAsync(new("reveal", Name: "github-token"), default));
        Assert.Empty(window.Editors); Assert.Empty(window.Copies); Assert.Empty(h.Prompts.Shown);
    }

    [Fact]
    public async Task SecretsAreEditedAndCopiedOnlyThroughTheNativeSide()
    {
        await using var h = new Harness();
        var (view, window) = View(h);
        window.Entries.Enqueue(new("npm-token", "npm publish token", "", new Secret(Value)));
        Assert.Null(await view.ExecuteAsync(new("add"), default));
        Assert.Null(Assert.Single(window.Editors)); Assert.Equal([null], window.EditorProblems);
        Assert.Equal(Value, h.Vault.Reveal("npm-token")!.Reveal());
        // A rule the vault enforces is shown inside the dialog; a cancelled dialog changes nothing.
        window.Entries.Enqueue(new("npm token", "", "", new Secret("x1y2z3")));
        await view.ExecuteAsync(new("add"), default);
        Assert.Contains("Names use letters", window.EditorProblems[^1]);
        window.Entries.Enqueue(null);
        await view.ExecuteAsync(new("add"), default);
        Assert.Single(h.Vault.Secrets());
        // Editing without a value keeps the stored one.
        window.Entries.Enqueue(new("npm-token", "npm token for releases", "publisher", null));
        Assert.Null(await view.ExecuteAsync(new("edit", Name: "npm-token"), default));
        Assert.Equal("npm publish token", window.Editors[^1]!.Description);
        var edited = Assert.Single(h.Vault.Secrets());
        Assert.Equal(("npm token for releases", "publisher", Value), (edited.Description, edited.Username, h.Vault.Reveal("npm-token")!.Reveal()));

        var copied = (await view.ExecuteAsync(new("copySecret", Name: "npm-token"), default))!;
        Assert.False(copied.Error); Assert.DoesNotContain(Value, copied.Text); Assert.Contains("30 seconds", copied.Text);
        Assert.Equal((Value, true), (window.Copies[^1].Text.Reveal(), window.Copies[^1].Sensitive));
        Assert.False((await view.ExecuteAsync(new("copyUsername", Name: "npm-token"), default))!.Error);
        Assert.Equal(("publisher", false), (window.Copies[^1].Text.Reveal(), window.Copies[^1].Sensitive));
        window.ClipboardAvailable = false;
        Assert.Equal(new("The clipboard is unavailable.", true), await view.ExecuteAsync(new("copySecret", Name: "npm-token"), default));

        window.Entries.Enqueue(new("npm-token", "npm token for releases", "", null));
        await view.ExecuteAsync(new("edit", Name: "npm-token"), default);
        Assert.Equal(new("npm-token has no username.", true), await view.ExecuteAsync(new("copyUsername", Name: "npm-token"), default));
    }

    [Fact]
    public async Task DeletingAsksFirstAndAGoneSecretIsRefusedWithoutADialog()
    {
        await using var h = new Harness();
        var (view, window) = View(h);
        h.Vault.Save(new("github-token", "GitHub token for CI", "", new Secret(Value)));
        h.Prompts.Confirmations.Enqueue(false);
        Assert.Null(await view.ExecuteAsync(new("delete", Name: "github-token"), default));
        Assert.NotNull(h.Vault.Reveal("github-token"));
        var prompt = Assert.IsType<ConfirmationPrompt>(Assert.Single(h.Prompts.Shown));
        Assert.Equal(("Delete", VaultView.Title), (prompt.Action, prompt.Title)); Assert.Contains("“github-token”", prompt.Message);
        h.Prompts.Confirmations.Enqueue(true);
        await view.ExecuteAsync(new("delete", Name: "github-token"), default);
        Assert.Null(h.Vault.Reveal("github-token"));

        foreach (var action in new[] { "edit", "delete", "copySecret", "copyUsername" })
            Assert.Equal(new("That secret no longer exists.", true), await view.ExecuteAsync(new(action, Name: "github-token"), default));
        Assert.Empty(window.Editors); Assert.Empty(window.Copies); Assert.Equal(2, h.Prompts.Shown.Count);
    }

    [Fact]
    public async Task AccessIsRevokedHereOrOnItsHostAndPendingScrubsAreForgottenOnRequest()
    {
        await using var h = new Harness();
        var (view, _) = View(h);
        await h.Vault.HandleAsync("dev", Add("deploy-key", AgentValue));
        var lease = Assert.Single(h.Vault.Leases());
        Assert.Equal(new("That access has already ended.", true), await view.ExecuteAsync(new("revokeLease", Id: "0000"), default));
        Assert.Null(await view.ExecuteAsync(new("revokeLease", Id: lease.Id), default));
        Assert.Empty(h.Vault.Leases()); Assert.Contains(h.Vault.PendingCleanups(), c => c.Instance == "dev" && c.Name == "deploy-key");

        h.Host.Edit(x => x.Leases.Add(new JsonObject { ["id"] = "l1", ["vm"] = "dev", ["name"] = "deploy-key", ["usesLeft"] = null, ["expiresAt"] = h.Now + 600000, ["reason"] = "", ["origin"] = "approved" }));
        await h.Hosts.RefreshAsync(default);
        Assert.Equal(new("That access has already ended.", true), await view.ExecuteAsync(new("revokeLease", Id: "l1"), default)); // not a lease of this PC
        Assert.Equal(new("That access has already ended.", true), await view.ExecuteAsync(new("revokeLease", Id: "l1", Host: "other_7462"), default));
        Assert.Empty(h.Host.Calls("DELETE", "/vault/leases/l1"));
        Assert.Null(await view.ExecuteAsync(new("revokeLease", Id: "l1", Host: Slug), default));
        Assert.Single(h.Host.Calls("DELETE", "/vault/leases/l1")); Assert.Empty(h.Hosts.Leases());

        Assert.Null(await view.ExecuteAsync(new("discardScrubs", Vm: "no-such-vm"), default));
        Assert.Empty(h.Prompts.Shown);
        h.Prompts.Confirmations.Enqueue(false);
        await view.ExecuteAsync(new("discardScrubs", Vm: "dev"), default);
        Assert.NotEmpty(h.Vault.PendingCleanups());
        h.Prompts.Confirmations.Enqueue(true);
        await view.ExecuteAsync(new("discardScrubs", Vm: "dev"), default);
        Assert.Empty(h.Vault.PendingCleanups());
        Assert.Equal("Forget scrubs", Assert.IsType<ConfirmationPrompt>(h.Prompts.Shown[^1]).Action);
    }

    [Fact]
    public async Task HostRequestsAskFirstAndReachTheirHost()
    {
        await using var h = new Harness();
        var (view, window) = View(h);
        Assert.Null(await view.ExecuteAsync(new("sync"), default));
        Assert.NotEmpty(h.Host.Calls("PUT", "/vault/settings")); Assert.Equal(VaultSync.Available, h.State.Mode);
        Assert.Null(await view.ExecuteAsync(new("setMode", Host: Slug, Mode: VaultSync.Available), default)); // already so: nothing to ask
        Assert.Empty(h.Prompts.Shown);
        var settings = h.Host.Calls("PUT", "/vault/settings").Length;
        h.Prompts.Confirmations.Enqueue(false);
        Assert.Null(await view.ExecuteAsync(new("setMode", Host: Slug, Mode: VaultSync.Locked), default));
        Assert.Equal(settings, h.Host.Calls("PUT", "/vault/settings").Length);
        h.Prompts.Confirmations.Enqueue(true);
        Assert.Null(await view.ExecuteAsync(new("setMode", Host: Slug, Mode: VaultSync.Locked), default));
        Assert.Equal("locked", h.Host.Calls("PUT", "/vault/settings")[^1].Body!["mode"]!.GetValue<string>());
        Assert.Equal("Lock to my PC", Assert.IsType<ConfirmationPrompt>(h.Prompts.Shown[^1]).Action);
        Assert.Equal(new("That host is no longer enrolled.", true), await view.ExecuteAsync(new("setMode", Host: "other_7462", Mode: VaultSync.Locked), default));
        h.Host.Forced["PUT /vault/settings"] = (400, new JsonObject { ["code"] = "invalid", ["title"] = "Refused", ["detail"] = "The host refused the mode." });
        h.Prompts.Confirmations.Enqueue(true);
        Assert.True((await view.ExecuteAsync(new("setMode", Host: Slug, Mode: VaultSync.Available), default))!.Error);

        h.Host.Edit(x => x.Devices.Add(new JsonObject { ["id"] = "d9", ["label"] = "Phone", ["createdAt"] = h.Now, ["lastUsedAt"] = null }));
        Assert.Null(await view.ExecuteAsync(new("refresh"), default));
        Assert.NotEmpty(h.Host.Calls("GET", "/vault/devices")); Assert.NotEmpty(h.Host.Calls("GET", "/vault/leases"));
        Assert.Equal(new("That device is no longer paired.", true), await view.ExecuteAsync(new("revokeDevice", Host: Slug, Id: "d1"), default));
        h.Prompts.Confirmations.Enqueue(true);
        Assert.Null(await view.ExecuteAsync(new("revokeDevice", Host: Slug, Id: "d9"), default));
        Assert.Single(h.Host.Calls("DELETE", "/vault/devices/d9"));
        Assert.Contains("“Phone”", Assert.IsType<ConfirmationPrompt>(h.Prompts.Shown[^1]).Message);

        h.Prompts.Picks.Enqueue(null); // the user closed the VM choice
        Assert.Null(await view.ExecuteAsync(new("pairPhone", Host: Slug), default));
        Assert.Empty(window.Pairings);
        h.Prompts.Picks.Enqueue([VaultHosts.ApprovalsOnly]); h.Prompts.Inputs.Enqueue("Tablet");
        Assert.Null(await view.ExecuteAsync(new("pairPhone", Host: Slug), default));
        Assert.Equal("Tablet", Assert.Single(window.Pairings).Label);
        Assert.Equal(new("That host is no longer enrolled.", true), await view.ExecuteAsync(new("pairPhone", Host: "other_7462"), default));

        Assert.Null(await view.ExecuteAsync(new("showKey"), default));
        Assert.Equal(VaultCrypto.Export(h.Key), Assert.Single(h.Prompts.Secrets).Value.Reveal());
        h.Prompts.Inputs.Enqueue("not a key");
        Assert.Equal(new("That is not a vault key (44 characters of base64).", true), await view.ExecuteAsync(new("importKey"), default));
    }

    [Fact]
    public async Task AnUnreadableVaultOffersAFreshStartOnlyWhileItIsUnreadable()
    {
        var clock = new FakeClock(); clock.Advance(TimeSpan.FromDays(20000));
        var files = new FakeFileSystem(clock); var protection = new FakeDataProtection(); var prompts = new FakePrompts(); var toasts = new FakeToastRaiser();
        VaultService Open() => new(new VaultStore(files, protection, VaultHostsTests.VaultPath), prompts, toasts, clock);
        await using (var first = Open()) first.Save(new("github-token", "", "", new Secret(Value)));
        protection.Denied = true;
        await using var vault = Open();
        var hosts = new VaultHosts(vault, new NoHosts(), prompts, clock);
        var view = new VaultView(vault, hosts, prompts, new FakeVaultWindow(), clock);
        var state = view.State();
        Assert.False(string.IsNullOrEmpty(state["unavailable"]!.GetValue<string>())); Assert.Empty(state["secrets"]!.AsArray());
        prompts.Confirmations.Enqueue(false);
        Assert.Null(await view.ExecuteAsync(new("reset"), default));
        Assert.NotNull(vault.Unavailable);
        prompts.Confirmations.Enqueue(true);
        var notice = (await view.ExecuteAsync(new("reset"), default))!;
        Assert.False(notice.Error); Assert.Contains("kept as", notice.Text);
        Assert.Null(vault.Unavailable); Assert.Null(view.State()["unavailable"]);
        Assert.Null(await view.ExecuteAsync(new("reset"), default));
        Assert.Equal(2, prompts.Shown.Count);
        await hosts.DisposeAsync();
    }
    private sealed class NoHosts : IVaultHostDirectory
    {
        public IReadOnlyList<VaultHostRef> Hosts() => [];
        public IReadOnlyList<VaultHostInstance> Instances(string slug) => [];
        public Task<string?> CheckUserAsync(string slug, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<ProcessResult> RunT3PairingAsync(string instance, CancellationToken cancellationToken) => Task.FromResult(new ProcessResult(1));
    }
}
