using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.Repatch;
using Construct.Companion.Core.Vault;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Runtime;
using static Construct.Companion.Tests.Runtime.ProcessSupervisorTests;
namespace Construct.Companion.Tests.Vault;

// The key vault on hosted VMs (docs/plans/key-vault-hosted.md): sync, unlock, host approvals and file
// decisions, the Hosts tab's actions. The host is an in-memory service behind FakeRemoteApi.
public sealed class VaultHostsTests
{
    internal const string VaultPath = "/fake/local/The-Construct-Vault/vault.dat", Slug = "host.example_7462";
    private static readonly string Pin = new('a', 64);

    // The vault routes of one host, with the contract's merge rule on PUT /vault/entries.
    internal sealed class FakeHost
    {
        private readonly object gate = new();
        public Dictionary<string, JsonObject> Entries { get; } = new(StringComparer.Ordinal);
        public string Mode { get; set; } = VaultSync.Available;
        public string? KeyCheck { get; set; }
        public string? WrappedKey { get; set; }
        public long Revision { get; set; } = 7;
        public List<string> UnlockedVms { get; } = [];
        public List<JsonObject> Approvals { get; } = [];
        public Dictionary<string, string> Decisions { get; } = new(StringComparer.Ordinal);
        public List<JsonObject> Files { get; } = [];
        public Dictionary<string, string> FileActions { get; } = new(StringComparer.Ordinal);
        public List<JsonObject> Leases { get; } = [];
        public List<JsonObject> Devices { get; } = [];
        public List<JsonObject> Events { get; } = [];
        // The approval page address the pairing reply names; WebUrlSource null = a host that predates the field.
        public string WebUrl { get; set; } = "https://vault.example.org/";
        public string? WebUrlSource { get; set; }
        public Dictionary<string, (int Status, JsonObject? Body)> Forced { get; } = new(StringComparer.Ordinal);
        public List<(string Method, string Path, JsonObject? Body)> Log { get; } = [];
        public (string Method, string Path, JsonObject? Body)[] Calls(string method, string path) { lock (gate) return Log.Where(l => l.Method == method && l.Path == path).ToArray(); }
        public void Edit(Action<FakeHost> change) { lock (gate) change(this); }
        public RemoteResponse Handle(RemoteRequest request)
        {
            var path = request.Url.AbsolutePath["/api/v1".Length..]; var body = request.Body is { } raw ? JsonNode.Parse(raw.GetRawText()) as JsonObject : null;
            lock (gate)
            {
                Log.Add((request.Method, path, body));
                if (Forced.TryGetValue(request.Method + " " + path, out var forced)) return new(forced.Status, forced.Body is null ? null : JsonSerializer.SerializeToElement(forced.Body));
                var id = path.Split('/')[^1];
                switch (request.Method, path)
                {
                    case ("GET", "/vault/entries"):
                        return Ok(new JsonObject { ["revision"] = Revision, ["mode"] = Mode, ["keyCheck"] = KeyCheck, ["unlockedVms"] = new JsonArray(UnlockedVms.Select(v => (JsonNode)v).ToArray()),
                            ["entries"] = new JsonArray(Entries.Values.Select(e => e.DeepClone()).ToArray()) });
                    case ("PUT", "/vault/entries"):
                        var applied = 0;
                        foreach (var e in body!["entries"]!.AsArray().OfType<JsonObject>())
                        {
                            var name = e["name"]!.GetValue<string>();
                            if (!Entries.TryGetValue(name, out var have) || VaultSync.Newer(e["updatedAt"]!.GetValue<long>(), e["updatedBy"]!.GetValue<string>(), have["updatedAt"]!.GetValue<long>(), have["updatedBy"]!.GetValue<string>()))
                            { Entries[name] = (JsonObject)e.DeepClone(); applied++; }
                        }
                        if (applied > 0) Revision++;
                        return Ok(new JsonObject { ["revision"] = Revision, ["applied"] = applied });
                    case ("PUT", "/vault/settings"):
                        Mode = body!["mode"]!.GetValue<string>(); WrappedKey = Mode == VaultSync.Available ? body["key"]?.GetValue<string>() : null; KeyCheck ??= body["keyCheck"]?.GetValue<string>();
                        return new(204);
                    case ("GET", "/vault/approvals"): return Ok(new JsonObject { ["approvals"] = new JsonArray(Approvals.Select(a => a.DeepClone()).ToArray()) });
                    case ("GET", "/vault/files"): return Ok(new JsonObject { ["files"] = new JsonArray(Files.Select(a => a.DeepClone()).ToArray()) });
                    case ("GET", "/vault/leases"): return Ok(new JsonObject { ["leases"] = new JsonArray(Leases.Select(a => a.DeepClone()).ToArray()) });
                    case ("GET", "/vault/devices"): return Ok(new JsonObject { ["devices"] = new JsonArray(Devices.Select(a => a.DeepClone()).ToArray()) });
                    case ("GET", "/vault/activity"): return Ok(new JsonObject { ["events"] = new JsonArray(Events.Select(a => a.DeepClone()).ToArray()) });
                    case ("POST", "/vault/devices"):
                        Devices.Add(new JsonObject { ["id"] = "d" + Devices.Count, ["label"] = body!["label"]!.GetValue<string>(), ["createdAt"] = 1730000000000, ["lastUsedAt"] = null });
                        var paired = new JsonObject { ["id"] = "d" + (Devices.Count - 1), ["token"] = new string('T', 43), ["webUrl"] = WebUrl };
                        if (WebUrlSource is not null) paired["webUrlSource"] = WebUrlSource;
                        return Ok(paired);
                }
                if (request.Method == "POST" && path.StartsWith("/vault/approvals/", StringComparison.Ordinal))
                {
                    if (Decisions.ContainsKey(id)) return new(409, JsonSerializer.SerializeToElement(new { code = "already-decided" }));
                    Decisions[id] = body!["decision"]!.GetValue<string>(); Approvals.RemoveAll(a => a["id"]!.GetValue<string>() == id); return new(204);
                }
                if (request.Method == "POST" && path.StartsWith("/vault/files/", StringComparison.Ordinal)) { FileActions[id] = body!["action"]!.GetValue<string>(); Files.RemoveAll(f => f["id"]!.GetValue<string>() == id); return new(204); }
                if (request.Method == "POST" && path.EndsWith("/vault/unlock", StringComparison.Ordinal)) { UnlockedVms.Add(path.Split('/')[2]); return new(204); }
                if (request.Method == "DELETE" && path.StartsWith("/vault/leases/", StringComparison.Ordinal)) { Leases.RemoveAll(l => l["id"]!.GetValue<string>() == id); return new(204); }
                if (request.Method == "DELETE" && path.StartsWith("/vault/devices/", StringComparison.Ordinal)) { Devices.RemoveAll(d => d["id"]!.GetValue<string>() == id); return new(204); }
                return new(404, JsonSerializer.SerializeToElement(new { code = "not-found" }));
            }
        }
        private static RemoteResponse Ok(JsonNode body) => new(200, JsonSerializer.SerializeToElement(body));
    }
    internal sealed class TestDirectory(RemoteHostClient client) : IVaultHostDirectory
    {
        public List<VaultHostRef> List { get; } = [new(Slug, "host.example", client)];
        public List<VaultHostInstance> Vms { get; } = [new("dev", Slug, "dev"), new("build", Slug, "build-vm")];
        public string? Problem { get; set; }
        public ProcessResult Pairing { get; set; } = new(0, """{"pairUrl":"http://192.0.2.5:5177/pair#token=direct","links":[{"kind":"direct","pairUrl":"http://192.0.2.5:5177/pair#token=direct"},{"kind":"forwarded","pairUrl":"https://host.example:40001/pair#token=t3-once"}]}""");
        public List<string> PairingRuns { get; } = [];
        public IReadOnlyList<VaultHostRef> Hosts() => List;
        public IReadOnlyList<VaultHostInstance> Instances(string slug) => Vms.Where(v => v.Slug == slug).ToArray();
        public Task<string?> CheckUserAsync(string slug, CancellationToken cancellationToken) => Task.FromResult(Problem);
        public Task<ProcessResult> RunT3PairingAsync(string instance, CancellationToken cancellationToken) { PairingRuns.Add(instance); return Task.FromResult(Pairing); }
    }
    internal sealed class Harness : IAsyncDisposable
    {
        public FakeClock Clock { get; } = new();
        public FakeFileSystem Files { get; }
        public FakeDataProtection Protection { get; } = new();
        public FakePrompts Prompts { get; } = new();
        public FakeToastRaiser Toasts { get; } = new();
        public FakeHost Host { get; } = new();
        public FakeRemoteApi Api { get; } = new() { Fingerprint = Pin };
        public VaultService Vault { get; }
        public TestDirectory Directory { get; }
        public VaultHosts Hosts { get; }
        public Harness(Action<Harness>? before = null)
        {
            Clock.Advance(TimeSpan.FromDays(20000)); Files = new(Clock); Api.Handler = Host.Handle;
            before?.Invoke(this);
            Vault = new(new VaultStore(Files, Protection, VaultPath), Prompts, Toasts, Clock);
            Directory = new(new RemoteHostClient(Api, Files, new FakeTokenStore(), "https://host.example:7462", RemoteAuthentication.Negotiate, Pin));
            Hosts = new(Vault, Directory, Prompts, Clock);
        }
        public byte[] Key => Vault.KeyCopy()!;
        public VaultHostState State => Vault.HostState(Slug);
        public Task Sync() => Hosts.SyncAsync(CancellationToken.None);
        public async Task Poll(bool slow = false) { await Hosts.PollAsync(slow, CancellationToken.None); await Hosts.DrainAsync(); }
        public long Now => VaultSync.Ms(Clock.UtcNow);
        // An entry as a VM's request leaves it on the host: sealed with K, written by vm:<name>.
        public void HostWrite(string name, string value, long at, string by = "vm:dev", string username = "") => HostWriteWith(Key, name, value, at, by, username);
        public void HostWriteWith(byte[] key, string name, string value, long at, string by = "vm:dev", string username = "") => Host.Edit(h => h.Entries[name] = VaultSync.ToJson(
            new VaultWireEntry(name, "from a VM", username.Length > 0, VaultCrypto.SealEntry(key, name, at, username, new Secret(value)), at, by, false)));
        public void HostDelete(string name, long at, string by = "vm:dev") => Host.Edit(h => h.Entries[name] = VaultSync.ToJson(new VaultWireEntry(name, "", false, null, at, by, true)));
        public async ValueTask DisposeAsync() { await Hosts.DisposeAsync(); await Vault.DisposeAsync(); }
    }
    private static string Open(byte[] key, JsonObject entry) =>
        VaultCrypto.OpenEntry(key, entry["name"]!.GetValue<string>(), entry["updatedAt"]!.GetValue<long>(), entry["payload"]!.GetValue<string>())!.Value.Value.Reveal();
    private static JsonObject Approval(string id, DateTimeOffset deadline) => new()
    {
        ["id"] = id, ["vm"] = "dev", ["op"] = "request", ["title"] = "Key vault — access request", ["message"] = "The VM “dev” asks for access to:\n  • github-token — CI\n\nReason given: “release”",
        ["action"] = "Approve", ["names"] = new JsonArray("github-token"), ["createdAt"] = VaultSync.Ms(deadline) - 600000, ["deadline"] = VaultSync.Ms(deadline)
    };

    [Fact]
    public void PayloadsRoundTripAndTheAadBindsNameAndTime()
    {
        var key = VaultCrypto.NewKey(); var other = VaultCrypto.NewKey();
        var payload = VaultCrypto.SealEntry(key, "github-token", 1730000000000, "ci-bot", new Secret("ghp_s3cret\nline two"));
        var raw = Convert.FromBase64String(payload);
        Assert.Equal(12 + 16 + Encoding.UTF8.GetByteCount("{\"username\":\"ci-bot\",\"secret\":\"ghp_s3cret\\nline two\"}"), raw.Length);
        var opened = VaultCrypto.OpenEntry(key, "github-token", 1730000000000, payload)!.Value;
        Assert.Equal(("ci-bot", "ghp_s3cret\nline two"), (opened.Username, opened.Value.Reveal()));
        Assert.Null(VaultCrypto.OpenEntry(key, "npm-token", 1730000000000, payload));     // moved to another name
        Assert.Null(VaultCrypto.OpenEntry(key, "github-token", 1730000000001, payload));  // replayed under another time
        Assert.Null(VaultCrypto.OpenEntry(other, "github-token", 1730000000000, payload)); // another vault key
        raw[^1] ^= 1; Assert.Null(VaultCrypto.OpenEntry(key, "github-token", 1730000000000, Convert.ToBase64String(raw)));
        Assert.Null(VaultCrypto.OpenEntry(key, "github-token", 1730000000000, "not base64!"));
        var check = VaultCrypto.KeyCheck(key);
        Assert.True(VaultCrypto.MatchesKeyCheck(key, check)); Assert.False(VaultCrypto.MatchesKeyCheck(other, check));
        Assert.NotEqual(check, VaultCrypto.KeyCheck(key)); // a fresh nonce every time
        Assert.Equal(key, VaultCrypto.Import(" " + VaultCrypto.Export(key).Replace('+', '-').Replace('/', '_').TrimEnd('=') + "\n"));
        Assert.Null(VaultCrypto.Import("c2hvcnQ="));
        Assert.True(VaultSync.Newer(2, "pc:a", 1, "vm:z")); Assert.True(VaultSync.Newer(1, "vm:a", 1, "pc:z")); Assert.False(VaultSync.Newer(1, "pc:a", 1, "pc:a"));
    }

    [Fact]
    public async Task FirstSyncCreatesTheKeyAndHandsTheHostItsKeyCheck()
    {
        await using var h = new Harness();
        h.Vault.Save(new("github-token", "GitHub token for CI", "ci-bot", new Secret("ghp_s3cretTokenValue")));
        Assert.False(h.Vault.HasKey);
        await h.Sync();
        Assert.True(h.Vault.HasKey);
        var settings = Assert.Single(h.Host.Calls("PUT", "/vault/settings")).Body!;
        Assert.Equal("available", settings["mode"]!.GetValue<string>());
        Assert.Equal(VaultCrypto.Export(h.Key), settings["key"]!.GetValue<string>());
        Assert.True(VaultCrypto.MatchesKeyCheck(h.Key, settings["keyCheck"]!.GetValue<string>()));
        var pushed = h.Host.Entries["github-token"];
        Assert.Equal("ghp_s3cretTokenValue", Open(h.Key, pushed)); Assert.True(pushed["hasUsername"]!.GetValue<bool>());
        Assert.Equal("pc:" + h.Vault.InstallId, pushed["updatedBy"]!.GetValue<string>());
        Assert.DoesNotContain("ghp_s3cret", pushed.ToJsonString());
        Assert.Equal(new VaultHostState("available", 8, h.Clock.UtcNow, "", false, h.Host.KeyCheck), h.State);
        // Nothing changed: the next pass reads but does not write.
        await h.Sync();
        Assert.Single(h.Host.Calls("PUT", "/vault/entries")); Assert.Single(h.Host.Calls("PUT", "/vault/settings"));
    }

    [Fact]
    public async Task ALockedChoiceIsKeptWhenAHostIsFirstSetUp()
    {
        await using var h = new Harness();
        h.Vault.UpdateHost(Slug, s => s with { Mode = VaultSync.Locked });
        await h.Sync();
        var settings = Assert.Single(h.Host.Calls("PUT", "/vault/settings")).Body!;
        Assert.Equal("locked", settings["mode"]!.GetValue<string>()); Assert.Null(settings["key"]); Assert.Null(h.Host.WrappedKey);
        Assert.Equal(VaultSync.Locked, h.State.Mode);
    }

    [Fact]
    public async Task SyncMergesBothWaysByLastWriterIncludingTombstones()
    {
        await using var h = new Harness();
        h.Vault.Save(new("github-token", "GitHub", "", new Secret("ghp_local_value_1")));
        h.Vault.Save(new("old-name", "Renamed below", "", new Secret("rename-me-123")));
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Vault.Save(new("new-name", "Renamed", "", null), "old-name");
        await h.Sync();
        Assert.True(h.Host.Entries["old-name"]["deleted"]!.GetValue<bool>()); Assert.Null(h.Host.Entries["old-name"]["payload"]);
        Assert.Equal("rename-me-123", Open(h.Key, h.Host.Entries["new-name"]));
        Assert.Equal(8, h.State.Revision);

        // A VM on the host added one secret and replaced another; an older write loses to this PC's.
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        h.HostWrite("db-pass", "Adm1nPassw0rd", h.Now - 1000, username: "admin");
        h.HostWrite("github-token", "ghp_rotated_by_vm", h.Now - 500);
        h.HostWrite("new-name", "stale-host-value", h.Now - 60000);
        await h.Sync();
        Assert.Equal("Adm1nPassw0rd", h.Vault.Reveal("db-pass")!.Reveal()); Assert.Equal("admin", h.Vault.Username("db-pass"));
        Assert.Equal("agent:dev", h.Vault.Secrets().Single(s => s.Name == "db-pass").Origin);
        Assert.Equal("ghp_rotated_by_vm", h.Vault.Reveal("github-token")!.Reveal());
        Assert.Equal("rename-me-123", h.Vault.Reveal("new-name")!.Reveal());
        Assert.Equal("rename-me-123", Open(h.Key, h.Host.Entries["new-name"])); // this PC's winner went back
        Assert.Equal(h.Host.Revision, h.State.Revision);
        Assert.Contains(h.Vault.Activity(), a => a.Host == "host.example" && a.Text.Contains("db-pass", StringComparison.Ordinal));

        // Deleting here writes a tombstone the host takes; a newer tombstone from the host deletes here.
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Vault.Delete("db-pass");
        h.HostDelete("github-token", h.Now + 5);
        await h.Sync();
        Assert.True(h.Host.Entries["db-pass"]["deleted"]!.GetValue<bool>());
        Assert.Null(h.Vault.Reveal("github-token"));
        // An older live copy elsewhere cannot bring a deleted secret back.
        h.HostWrite("github-token", "ghp_resurrected", h.Now - 100000);
        await h.Sync();
        Assert.Null(h.Vault.Reveal("github-token")); Assert.True(h.Host.Entries["github-token"]["deleted"]!.GetValue<bool>());

        // The document keeps all of it across a restart; tombstones expire after 30 days.
        await using var reopened = new VaultService(new VaultStore(h.Files, h.Protection, VaultPath), h.Prompts, h.Toasts, h.Clock);
        Assert.Equal(h.Vault.InstallId, reopened.InstallId); Assert.Equal(h.State, reopened.HostState(Slug));
        Assert.Equal(VaultCrypto.Export(h.Key), reopened.ExportKey()!.Reveal());
        h.Clock.Advance(VaultSync.TombstoneLifetime + TimeSpan.FromMinutes(1)); reopened.Sweep();
        h.Host.Edit(x => x.Entries.Clear());
        await using var fresh = new VaultHosts(reopened, h.Directory, h.Prompts, h.Clock);
        await fresh.SyncAsync(default);
        Assert.Equal(["new-name"], h.Host.Entries.Keys);
    }

    [Fact]
    public async Task ANewerValueFromTheHostEndsLocalLeasesAndTamperedEntriesAreRejected()
    {
        await using var h = new Harness();
        h.Vault.Save(new("github-token", "GitHub", "", new Secret("ghp_local_value_1")));
        await h.Sync();
        h.Prompts.Approvals.Enqueue(true);
        var request = VaultProtocol.ParseRequest("""{"v":1,"id":"1700000000000-1-1","op":"request","names":["github-token"],"uses":3}""").Request!;
        Assert.Equal("ok", (await h.Vault.HandleAsync("local-vm", request))["status"]!.GetValue<string>());
        Assert.Single(h.Vault.Leases());
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.HostWrite("github-token", "ghp_rotated_by_vm", h.Now);
        // Valid payloads under the wrong name or time do not open: the entry is skipped, not stored.
        var moved = VaultCrypto.SealEntry(h.Key, "other", h.Now, "", new Secret("moved-value-123"));
        h.Host.Edit(x => x.Entries["moved"] = VaultSync.ToJson(new("moved", "", false, moved, h.Now, "vm:dev", false)));
        await h.Sync();
        Assert.Equal("ghp_rotated_by_vm", h.Vault.Reveal("github-token")!.Reveal());
        Assert.Empty(h.Vault.Leases()); Assert.Contains(h.Vault.PendingCleanups(), c => c.Instance == "local-vm");
        Assert.Null(h.Vault.Reveal("moved"));
        Assert.Contains("moved", h.State.LastError);
        Assert.Contains(h.Vault.Activity(), a => a.Warning && a.Text.Contains("moved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHostWithAnotherKeyNeedsTheKeyAndIsNeverWritten()
    {
        var foreign = VaultCrypto.NewKey();
        await using var h = new Harness(x => x.Host.KeyCheck = VaultCrypto.KeyCheck(foreign));
        h.Vault.Save(new("github-token", "GitHub", "", new Secret("ghp_local_value_1")));
        await h.Sync();
        Assert.False(h.Vault.HasKey); // never invented while a host already has one
        Assert.True(h.State.NeedsKey); Assert.Contains("Import vault key", h.State.LastError);
        Assert.Empty(h.Host.Calls("PUT", "/vault/entries")); Assert.Empty(h.Host.Calls("PUT", "/vault/settings"));
        Assert.True(Assert.Single(h.Hosts.Views()).State.NeedsKey);
        Assert.Equal("This host's vault uses a key this PC does not have: import it (Import vault key…) from the PC that set it up.", await h.Hosts.SetModeAsync(Slug, VaultSync.Locked, default));
        Assert.Contains("import it", await h.Hosts.ShowKeyAsync(default));

        // This PC's own key does not match either: still never pushed.
        h.Vault.ImportKey(VaultCrypto.NewKey());
        await h.Sync();
        Assert.True(h.State.NeedsKey); Assert.Empty(h.Host.Calls("PUT", "/vault/entries"));
        // No unlock with a key the host would reject.
        h.Hosts.Online(new("dev", Slug, "dev"), true); await h.Hosts.AfterStartAsync(new("dev", Slug, "dev"), default); await h.Hosts.DrainAsync();
        Assert.Empty(h.Host.Calls("POST", "/vms/dev/vault/unlock"));
    }

    [Fact]
    public async Task ImportedKeysMustOpenAHostsKeyCheck()
    {
        var shared = VaultCrypto.NewKey();
        await using var h = new Harness(x => x.Host.KeyCheck = VaultCrypto.KeyCheck(shared));
        h.HostWriteWith(shared, "github-token", "ghp_from_other_pc", 1730000000000);
        Assert.Equal("That is not a vault key (44 characters of base64).", await h.Hosts.ImportKeyAsync("not a key", default));
        Assert.Equal("This key does not open the vault on any of your hosts.", await h.Hosts.ImportKeyAsync(VaultCrypto.Export(VaultCrypto.NewKey()), default));
        Assert.False(h.Vault.HasKey);
        h.Prompts.Inputs.Enqueue(VaultCrypto.Export(shared));
        Assert.Null(await h.Hosts.ImportKeyAsync(default));
        Assert.True(((InputPrompt)h.Prompts.Shown[^1]).Password);
        Assert.Equal(shared, h.Key); Assert.Equal(h.Host.KeyCheck, h.State.KeyCheck); Assert.False(h.State.NeedsKey);
        await h.Sync();
        Assert.Equal("ghp_from_other_pc", h.Vault.Reveal("github-token")!.Reveal());
        Assert.Empty(h.Host.Calls("PUT", "/vault/settings"));
        // The same key again changes nothing and asks nothing.
        Assert.Null(await h.Hosts.ImportKeyAsync(VaultCrypto.Export(shared), default));
        Assert.DoesNotContain(h.Prompts.Shown, p => p is ConfirmationPrompt);
        await h.Hosts.ShowKeyAsync(default);
        var shown = Assert.Single(h.Prompts.Secrets);
        Assert.Equal(VaultCrypto.Export(shared), shown.Value.Reveal()); Assert.Contains("Import vault key", shown.Note);
    }

    [Fact]
    public async Task UnlockFollowsLockedHostsOnlineTransitionsAndEveryStart()
    {
        await using var h = new Harness();
        await h.Sync();
        var dev = new VaultHostInstance("dev", Slug, "dev");
        h.Hosts.Online(dev, true); await h.Hosts.DrainAsync();
        Assert.Empty(h.Host.Calls("POST", "/vms/dev/vault/unlock")); // always available: nothing to open

        h.Host.Edit(x => x.Mode = VaultSync.Locked); h.Hosts.Online(dev, false);
        await h.Sync();
        Assert.Equal(VaultSync.Locked, h.State.Mode);
        h.Hosts.Online(dev, true); await h.Hosts.DrainAsync();
        var unlock = Assert.Single(h.Host.Calls("POST", "/vms/dev/vault/unlock"));
        Assert.Equal(VaultCrypto.Export(h.Key), unlock.Body!["key"]!.GetValue<string>());
        h.Hosts.Online(dev, true); await h.Hosts.DrainAsync();
        Assert.Single(h.Host.Calls("POST", "/vms/dev/vault/unlock")); // still online: no transition
        h.Hosts.Online(dev, false); h.Hosts.Online(dev, true); await h.Hosts.DrainAsync();
        Assert.Equal(2, h.Host.Calls("POST", "/vms/dev/vault/unlock").Length);

        // A pass opens the locked vault for online VMs the host does not list as unlocked (a restart it missed).
        h.Host.Edit(x => x.UnlockedVms.Clear());
        await h.Sync();
        Assert.Equal(3, h.Host.Calls("POST", "/vms/dev/vault/unlock").Length);
        await h.Sync();
        Assert.Equal(3, h.Host.Calls("POST", "/vms/dev/vault/unlock").Length);

        // After the Companion starts a VM it always unlocks (a no-op on an available host); failures are only logged.
        await h.Hosts.AfterStartAsync(new("build", Slug, "build-vm"), default);
        Assert.Single(h.Host.Calls("POST", "/vms/build-vm/vault/unlock"));
        h.Host.Forced["POST /vms/build-vm/vault/unlock"] = (409, new JsonObject { ["code"] = "not-running", ["detail"] = "rejected " + VaultCrypto.Export(h.Key) });
        await h.Hosts.AfterStartAsync(new("build", Slug, "build-vm"), default);
        var logged = Assert.Single(h.Vault.Activity(), a => a.Warning && a.Text.StartsWith("Could not unlock", StringComparison.Ordinal));
        Assert.Equal("build-vm", logged.Instance); Assert.DoesNotContain(VaultCrypto.Export(h.Key), logged.Text);
    }

    [Fact]
    public async Task AHostedInstanceSeenFirstSyncsAtOnceAndLocalChangesSyncTwoSecondsLater()
    {
        await using var h = new Harness();
        using var stop = new CancellationTokenSource();
        var run = h.Hosts.RunAsync(stop.Token);
        await Eventually(() => h.Clock.PendingDelays == 2); // sync wait + approval poll wait
        Assert.Empty(h.Host.Calls("GET", "/vault/entries"));
        h.Hosts.Online(new("dev", Slug, "dev"), true);
        await Eventually(() => h.Host.Calls("GET", "/vault/entries").Length == 1 && h.State.LastSyncAt is not null);
        h.Vault.Save(new("npm", "npm publish token", "", new Secret("npm_abcdefgh")));
        await Eventually(() => h.Clock.PendingDelays >= 2);
        Assert.Single(h.Host.Calls("GET", "/vault/entries"));
        h.Clock.Advance(VaultHosts.ChangeDelay);
        await Eventually(() => h.Host.Entries.ContainsKey("npm"));
        // Then every five minutes.
        await Eventually(() => h.Host.Calls("GET", "/vault/entries").Length == 2 && h.Clock.PendingDelays >= 2);
        h.Clock.Advance(VaultHosts.SyncInterval);
        await Eventually(() => h.Host.Calls("GET", "/vault/entries").Length == 3);
        // The approval poll ran for the online VM.
        await Eventually(() => h.Host.Calls("GET", "/vault/approvals").Length > 0);
        await stop.CancelAsync(); await run;
    }

    [Fact]
    public async Task HostApprovalsShareTheDialogQueueAndPostTheAnswer()
    {
        await using var h = new Harness();
        h.Host.Edit(x => x.Approvals.Add(Approval("a1", h.Clock.UtcNow + TimeSpan.FromMinutes(10))));
        await h.Poll();
        Assert.Empty(h.Host.Calls("GET", "/vault/approvals")); // no VM of this user online on that host
        h.Hosts.Online(new("dev", Slug, "dev"), true);
        h.Prompts.Approvals.Enqueue(true);
        await h.Poll();
        var prompt = Assert.IsType<ApprovalPrompt>(Assert.Single(h.Prompts.Shown));
        Assert.Equal("Key vault — access request", prompt.Title); Assert.Equal("Approve", prompt.Action);
        Assert.Equal("The VM “dev” asks for access to:\n• github-token — CI\n\nReason given: “release”", prompt.Message);
        Assert.Equal("approve", h.Host.Decisions["a1"]);
        h.Host.Edit(x => x.Approvals.Add(Approval("a2", h.Clock.UtcNow + TimeSpan.FromMinutes(10))));
        h.Prompts.Approvals.Enqueue(false);
        await h.Poll(); await h.Poll();
        Assert.Equal("deny", h.Host.Decisions["a2"]); Assert.Equal(2, h.Prompts.Shown.Count);

        // A host approval waits behind a local VM's dialog: one dialog at a time.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var shown = 0;
        h.Prompts.ApprovalHandler = (_, _) => { Interlocked.Increment(ref shown); return gate.Task; };
        h.Vault.Save(new("github-token", "CI", "", new Secret("ghp_local_value_1")));
        var local = h.Vault.HandleAsync("local-vm", VaultProtocol.ParseRequest("""{"v":1,"id":"1700000000000-1-2","op":"get","names":["github-token"]}""").Request!);
        await Eventually(() => shown == 1);
        h.Host.Edit(x => x.Approvals.Add(Approval("a3", h.Clock.UtcNow + TimeSpan.FromMinutes(10))));
        await h.Hosts.PollAsync(false, default); await Task.Delay(30);
        Assert.Equal(1, shown);
        gate.SetResult(true); await local; await h.Hosts.DrainAsync();
        Assert.Equal(2, shown); Assert.Equal("approve", h.Host.Decisions["a3"]);
    }

    [Fact]
    public async Task AnApprovalAnsweredElsewhereClosesItsDialogAndLateAnswersAreSilent()
    {
        await using var h = new Harness();
        h.Hosts.Online(new("dev", Slug, "dev"), true);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Prompts.ApprovalHandler = async (_, token) => { try { await Task.Delay(Timeout.Infinite, token); } finally { closed.TrySetResult(); } return true; };
        h.Host.Edit(x => x.Approvals.Add(Approval("a1", h.Clock.UtcNow + TimeSpan.FromMinutes(10))));
        await h.Hosts.PollAsync(false, default);
        await Eventually(() => h.Prompts.Shown.Count == 1);
        h.Host.Edit(x => x.Approvals.Clear()); // the phone answered
        await h.Poll();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(h.Host.Calls("POST", "/vault/approvals/a1"));

        // The phone was faster while the dialog was open: 409 is not an error.
        h.Prompts.ApprovalHandler = null; h.Prompts.Approvals.Enqueue(true);
        h.Host.Edit(x => { x.Approvals.Add(Approval("a2", h.Clock.UtcNow + TimeSpan.FromMinutes(10))); x.Decisions["a2"] = "deny"; });
        await h.Poll();
        Assert.Single(h.Host.Calls("POST", "/vault/approvals/a2"));
        Assert.DoesNotContain(h.Vault.Activity(), a => a.Warning);
        // Any other failure is recorded.
        h.Prompts.Approvals.Enqueue(true);
        h.Host.Edit(x => x.Approvals.Add(Approval("a3", h.Clock.UtcNow + TimeSpan.FromMinutes(10))));
        h.Host.Forced["POST /vault/approvals/a3"] = (500, null);
        await h.Poll();
        Assert.Contains(h.Vault.Activity(), a => a.Warning && a.Text.StartsWith("Could not send your answer", StringComparison.Ordinal));
        // An expired approval closes at its deadline without an answer.
        h.Prompts.ApprovalHandler = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return true; };
        h.Host.Edit(x => x.Approvals.Add(Approval("a4", h.Clock.UtcNow + TimeSpan.FromMinutes(1))));
        await h.Hosts.PollAsync(false, default);
        await Eventually(() => h.Prompts.Shown.Count == 4 && h.Clock.PendingDelays > 0);
        h.Clock.Advance(TimeSpan.FromMinutes(1)); await h.Hosts.DrainAsync();
        Assert.Empty(h.Host.Calls("POST", "/vault/approvals/a4"));
    }

    [Fact]
    public async Task AnotherAppsAnswerToAHostApprovalGoesToTheHostOnce()
    {
        await using var h = new Harness();
        h.Hosts.Online(new("dev", Slug, "dev"), true);
        var closed = 0;
        h.Prompts.ApprovalHandler = async (_, token) => { try { await Task.Delay(Timeout.Infinite, token); return true; } finally { Interlocked.Increment(ref closed); } };
        var deadline = h.Clock.UtcNow + TimeSpan.FromMinutes(10);
        h.Host.Edit(x => x.Approvals.Add(Approval("a1", deadline)));
        await h.Hosts.PollAsync(false, default);
        await Eventually(() => h.Prompts.Shown.Count == 1);
        var listed = Assert.Single(h.Vault.PendingApprovals());
        Assert.Equal(("dev", "dev", "host", Slug, null, "a1", "request"), (listed.Instance, listed.Vm, listed.Kind, listed.Host, listed.RequestId, listed.HostRequestId, listed.Op));
        Assert.Equal(["github-token"], listed.Names); Assert.Equal(deadline - TimeSpan.FromMinutes(10), listed.CreatedAt); Assert.Equal(deadline, listed.Deadline);
        Assert.Equal(("Key vault — access request", "Approve", "Deny"), (listed.Title, listed.Action, listed.Deny));
        Assert.Equal(VaultDecision.Decided, await h.Vault.DecideAsync(listed.Id, false));
        await h.Hosts.DrainAsync();
        Assert.Equal("deny", h.Host.Decisions["a1"]); Assert.Single(h.Host.Calls("POST", "/vault/approvals/a1"));
        Assert.Equal(1, closed); Assert.Empty(h.Vault.PendingApprovals());
        Assert.Contains(h.Vault.Activity(), a => a.Host == "host.example" && a.Text == "Denied access to github-token from another app on this PC.");
        await h.Poll(); Assert.Single(h.Prompts.Shown); // answered: never asked again

        // The phone was faster: the host's 409 is the answer, the dialog still closes, no warning.
        h.Host.Edit(x => x.Approvals.Add(Approval("a2", deadline)));
        await h.Hosts.PollAsync(false, default);
        await Eventually(() => h.Prompts.Shown.Count == 2);
        h.Host.Edit(x => x.Decisions["a2"] = "approve");
        Assert.Equal(VaultDecision.AlreadyDecided, await h.Vault.DecideAsync(h.Vault.PendingApprovals()[0].Id, true));
        await h.Hosts.DrainAsync();
        Assert.Equal(2, closed); Assert.DoesNotContain(h.Vault.Activity(), a => a.Warning);
        // Expired on the host: 404. Any other failure is a warning and Failed.
        foreach (var (id, status, expected) in new[] { ("a3", 404, VaultDecision.NotFound), ("a4", 500, VaultDecision.Failed) })
        {
            h.Host.Edit(x => x.Approvals.Add(Approval(id, deadline)));
            h.Host.Forced["POST /vault/approvals/" + id] = (status, null);
            await h.Hosts.PollAsync(false, default);
            await Eventually(() => h.Vault.PendingApprovals().Any(a => a.HostRequestId == id));
            Assert.Equal(expected, await h.Vault.DecideAsync(h.Vault.PendingApprovals().Single(a => a.HostRequestId == id).Id, true));
            await h.Hosts.DrainAsync();
        }
        Assert.Contains(h.Vault.Activity(), a => a.Warning && a.Text.StartsWith("Could not send your answer", StringComparison.Ordinal));

        // A dialog answer goes to the host as before, and a later decision is too late.
        string? id5 = null;
        h.Prompts.ApprovalHandler = (_, _) => { id5 = h.Vault.PendingApprovals().Single(a => a.HostRequestId == "a5").Id; return Task.FromResult(true); };
        h.Host.Edit(x => x.Approvals.Add(Approval("a5", deadline)));
        await h.Poll();
        Assert.Equal("approve", h.Host.Decisions["a5"]);
        Assert.Equal(VaultDecision.AlreadyDecided, await h.Vault.DecideAsync(id5!, false));
        Assert.Single(h.Host.Calls("POST", "/vault/approvals/a5"));
    }

    [Fact]
    public async Task HostFileDecisionsOpenOneGridPerHostAndPostEveryChoice()
    {
        await using var h = new Harness();
        h.Hosts.Online(new("dev", Slug, "dev"), true);
        h.Host.Edit(x =>
        {
            x.Files.Add(new JsonObject { ["id"] = "f1", ["vm"] = "dev", ["path"] = "/root/repo/.env", ["names"] = new JsonArray("github-token"), ["type"] = "text", ["size"] = 30, ["createdAt"] = 1 });
            x.Files.Add(new JsonObject { ["id"] = "f2", ["vm"] = "dev", ["path"] = "/root/notes.db", ["names"] = new JsonArray("github-token"), ["type"] = "sqlite", ["size"] = 4096, ["createdAt"] = 1 });
        });
        await h.Poll();
        Assert.Empty(h.Host.Calls("GET", "/vault/files")); // every fifth round only
        h.Prompts.FileDecisions.Enqueue(new Dictionary<string, string> { ["f1"] = "redact", ["f2"] = "bogus" });
        await h.Poll(slow: true);
        var grid = Assert.IsType<FileDecisionPrompt>(Assert.Single(h.Prompts.Shown));
        Assert.Equal("Key vault — secrets left on “dev”", grid.Title);
        Assert.Equal(["/root/repo/.env", "/root/notes.db"], grid.Files.Select(f => f.Path));
        Assert.Equal("github-token · SQLite database · 4 KB", grid.Files[1].Detail);
        Assert.Equal(new Dictionary<string, string> { ["f1"] = "redact", ["f2"] = "keep" }, h.Host.FileActions);
        // Dismissing the grid keeps every file, and still answers each one.
        h.Host.Edit(x => x.Files.Add(new JsonObject { ["id"] = "f3", ["vm"] = "dev", ["path"] = "/root/x", ["names"] = new JsonArray("npm"), ["type"] = "text", ["size"] = 1 }));
        await h.Poll(slow: true);
        Assert.Equal("keep", h.Host.FileActions["f3"]); Assert.Equal(2, h.Prompts.Shown.Count);
        await h.Poll(slow: true);
        Assert.Equal(2, h.Prompts.Shown.Count);
    }

    [Fact]
    public async Task HostActivityJoinsTheListAndScrubReportsRaiseToasts()
    {
        await using var h = new Harness();
        h.Host.Edit(x => x.Events.Add(new JsonObject { ["at"] = h.Now - 60000, ["vm"] = "dev", ["text"] = "Redacted old-token from 1 file of agent logs.", ["warning"] = false }));
        await h.Sync(); // the first read only catches up
        Assert.Contains(h.Vault.Activity(), a => a.Host == "host.example" && a.Instance == "dev" && a.Text.StartsWith("Redacted old-token", StringComparison.Ordinal));
        Assert.Empty(h.Toasts.Toasts);
        h.Hosts.Online(new("dev", Slug, "dev"), true);
        h.Host.Edit(x =>
        {
            x.Events.Add(new JsonObject { ["at"] = h.Now, ["vm"] = "dev", ["text"] = "Redacted github-token from 2 files of agent logs.", ["warning"] = false });
            x.Events.Add(new JsonObject { ["at"] = h.Now, ["vm"] = "dev", ["text"] = "Could not clean /root/repo/.env.", ["warning"] = true });
        });
        await h.Poll(slow: true); await h.Poll(slow: true);
        Assert.Equal(2, h.Toasts.Toasts.Count);
        Assert.Contains("Redacted github-token from 2 files of agent logs on “dev”.", h.Toasts.Toasts[0].Xml);
        Assert.Contains("Secret scrub incomplete", h.Toasts.Toasts[1].Xml);
        Assert.Equal(3, h.Vault.Activity().Count(a => a.Host == "host.example" && a.Instance == "dev"));
    }

    [Fact]
    public async Task ModeSwitchesSendTheKeyOnlyForAlwaysAvailableAndUnlockRunningVms()
    {
        await using var h = new Harness();
        Assert.Equal("Unknown mode.", await h.Hosts.SetModeAsync(Slug, "sometimes", default));
        h.Hosts.Online(new("dev", Slug, "dev"), true); await h.Hosts.DrainAsync();
        Assert.Null(await h.Hosts.SetModeAsync(Slug, VaultSync.Locked, default)); // syncs first: this PC had no key yet
        var locked = h.Host.Calls("PUT", "/vault/settings")[^1].Body!;
        Assert.Equal("locked", locked["mode"]!.GetValue<string>()); Assert.Null(locked["key"]); Assert.Equal(h.State.KeyCheck, locked["keyCheck"]!.GetValue<string>());
        Assert.Null(h.Host.WrappedKey); Assert.Equal(VaultSync.Locked, h.State.Mode);
        Assert.Single(h.Host.Calls("POST", "/vms/dev/vault/unlock"));
        Assert.Null(await h.Hosts.SetModeAsync(Slug, VaultSync.Available, default));
        Assert.Equal(VaultCrypto.Export(h.Key), h.Host.Calls("PUT", "/vault/settings")[^1].Body!["key"]!.GetValue<string>());
        Assert.Equal(VaultSync.Available, h.State.Mode); Assert.NotNull(h.Host.WrappedKey);
        // A refusal is shown; the key it echoed is not.
        h.Host.Forced["PUT /vault/settings"] = (400, new JsonObject { ["code"] = "invalid", ["title"] = "Bad key", ["detail"] = "key " + VaultCrypto.Export(h.Key) });
        var problem = await h.Hosts.SetModeAsync(Slug, VaultSync.Available, default);
        Assert.NotNull(problem); Assert.DoesNotContain(VaultCrypto.Export(h.Key), problem);
        Assert.Equal("That host is no longer enrolled.", await h.Hosts.SetModeAsync("other_7462", VaultSync.Locked, default));
    }

    [Fact]
    public async Task HostLeasesAndDevicesAreListedAndRevoked()
    {
        await using var h = new Harness();
        h.Host.Edit(x =>
        {
            x.Leases.Add(new JsonObject { ["id"] = "l1", ["vm"] = "dev", ["name"] = "github-token", ["usesLeft"] = 2, ["expiresAt"] = h.Now + 3600000, ["reason"] = "release", ["origin"] = "approved" });
            x.Devices.Add(new JsonObject { ["id"] = "d9", ["label"] = "Phone", ["createdAt"] = h.Now, ["lastUsedAt"] = null });
        });
        await h.Hosts.RefreshAsync(default);
        var lease = Assert.Single(h.Hosts.Leases());
        Assert.Equal(("host.example", "dev", "github-token", (int?)2, "release"), (lease.Host, lease.Lease.Vm, lease.Lease.Name, lease.Lease.UsesLeft, lease.Lease.Reason));
        Assert.Equal("Phone", Assert.Single(Assert.Single(h.Hosts.Views()).Devices).Label);
        Assert.Equal(["dev", "build"], h.Hosts.Views()[0].Instances);
        Assert.Null(await h.Hosts.RevokeLeaseAsync(Slug, "l1", default));
        Assert.Single(h.Host.Calls("DELETE", "/vault/leases/l1")); Assert.Empty(h.Hosts.Leases());
        Assert.Null(await h.Hosts.RevokeDeviceAsync(Slug, "d9", default));
        Assert.Empty(h.Hosts.Views()[0].Devices);
    }

    [Fact]
    public async Task PairingAPhoneMintsTheProxyOrForwardedT3LinkAndPutsBothTokensInTheFragment()
    {
        await using var h = new Harness();
        h.Prompts.Picks.Enqueue(["build"]); h.Prompts.Inputs.Enqueue("  Pixel\n");
        var pairing = (await h.Hosts.PairPhoneAsync(Slug, default))!;
        var pick = Assert.IsType<PickPrompt>(h.Prompts.Shown[0]);
        Assert.Equal(["dev", "build", VaultHosts.ApprovalsOnly], pick.Items.Select(i => i.Id));
        Assert.Equal(["build"], h.Directory.PairingRuns);
        Assert.Equal("Pixel", Assert.Single(h.Host.Calls("POST", "/vault/devices")).Body!["label"]!.GetValue<string>());
        Assert.Equal("https://vault.example.org/vault/pair#token=" + new string('T', 43) + "&next=" + Uri.EscapeDataString("https://host.example:40001/pair#token=t3-once"), pairing.Url.Reveal());
        Assert.Equal(("host.example", "Pixel", "build-vm"), (pairing.Host, pairing.Label, pairing.Vm));
        Assert.DoesNotContain("TTTT", pairing.ToString());
        Assert.Equal("Pixel", Assert.Single(h.Hosts.Views()[0].Devices).Label);

        h.Prompts.Picks.Enqueue([VaultHosts.ApprovalsOnly]); h.Prompts.Inputs.Enqueue("");
        var approvalsOnly = (await h.Hosts.PairPhoneAsync(Slug, default))!;
        Assert.Equal("https://vault.example.org/vault/pair#token=" + new string('T', 43), approvalsOnly.Url.Reveal());
        Assert.Equal(("Phone", ""), (approvalsOnly.Label, approvalsOnly.Vm)); Assert.Single(h.Directory.PairingRuns);

        // The address the user reaches T3 Code at through their own proxy wins over the forward.
        h.Directory.Pairing = new(0, """{"pairUrl":"https://host.example:40001/pair#token=t3-fwd","links":[{"kind":"proxy","pairUrl":"https://t3.example.net:8443/pair#token=t3-proxy"},{"kind":"forwarded","pairUrl":"https://host.example:40001/pair#token=t3-fwd"}]}""");
        h.Prompts.Picks.Enqueue(["dev"]); h.Prompts.Inputs.Enqueue("Tablet");
        var proxied = (await h.Hosts.PairPhoneAsync(Slug, default))!;
        Assert.EndsWith("&next=" + Uri.EscapeDataString("https://t3.example.net:8443/pair#token=t3-proxy"), proxied.Url.Reveal());

        // No forwarded link (or no forward yet): nothing is paired.
        h.Directory.Pairing = new(7);
        h.Prompts.Picks.Enqueue(["dev"]); h.Prompts.Inputs.Enqueue("Phone");
        Assert.Contains("port forward", (await Assert.ThrowsAsync<InvalidOperationException>(() => h.Hosts.PairPhoneAsync(Slug, default))).Message);
        h.Directory.Pairing = new(0, """{"links":[{"kind":"direct","pairUrl":"http://192.0.2.5:5177/pair#token=x"}]}""");
        h.Prompts.Picks.Enqueue(["dev"]); h.Prompts.Inputs.Enqueue("Phone");
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Hosts.PairPhoneAsync(Slug, default));
        Assert.Equal(3, h.Host.Calls("POST", "/vault/devices").Length);
        h.Prompts.Picks.Enqueue(null);
        Assert.Null(await h.Hosts.PairPhoneAsync(Slug, default));
    }

    [Fact]
    public async Task ThePairingDialogPointsAtTheHostSettingOnlyWhilePhonesGetTheSelfSignedAddress()
    {
        await using var h = new Harness();
        async Task<VaultPairing> Pair()
        {
            h.Prompts.Picks.Enqueue(["dev"]); h.Prompts.Inputs.Enqueue("Phone");
            return (await h.Hosts.PairPhoneAsync(Slug, default))!;
        }
        // A configured proxy (or an older host whose address differs from the service's): no note.
        Assert.Equal("", (await Pair()).Note);
        h.Host.WebUrlSource = "hostConfig"; h.Host.WebUrl = "https://vault.example.net";
        Assert.Equal("", (await Pair()).Note);
        h.Host.WebUrlSource = "option";
        Assert.Equal("", (await Pair()).Note);

        // The host service's own address: the dialog says so and where a host admin changes it.
        h.Host.WebUrlSource = "default"; h.Host.WebUrl = "https://host.example:7462";
        var own = await Pair();
        Assert.StartsWith("https://host.example:7462/vault/pair#token=", own.Url.Reveal());
        Assert.Contains("https://host.example:7462", own.Note);
        Assert.Contains("self-signed", own.Note);
        Assert.Contains("Host Administration → Configuration → Key vault → Approval page address", own.Note);
        // An older host that does not say: its own address is the one the Companion talks to.
        h.Host.WebUrlSource = null;
        Assert.Contains("Approval page address", (await Pair()).Note);
        // A non-https answer falls back to the service address, which is self-signed as well.
        h.Host.WebUrl = "http://vault.example.net"; h.Host.WebUrlSource = "hostConfig";
        var fallback = await Pair();
        Assert.StartsWith("https://host.example:7462/vault/pair#", fallback.Url.Reveal());
        Assert.NotEqual("", fallback.Note);
    }

    [Fact]
    public async Task AT3LinkOnTheApprovalPagesOriginIsNeverHandedOut()
    {
        await using var h = new Harness();
        h.Host.WebUrl = "https://shared.example.net"; h.Host.WebUrlSource = "hostConfig";
        h.Directory.Pairing = new(0, """{"pairUrl":"https://host.example:40001/pair#token=fwd","links":[{"kind":"proxy","pairUrl":"https://SHARED.example.net:443/pair#token=t3"},{"kind":"forwarded","pairUrl":"https://host.example:40001/pair#token=fwd"}]}""");
        h.Prompts.Picks.Enqueue(["dev"]); h.Prompts.Inputs.Enqueue("Phone");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Hosts.PairPhoneAsync(Slug, default));
        Assert.Equal(VaultSync.SameOriginError, error.Message);
        // The device it had to pair first is revoked again.
        Assert.Single(h.Host.Calls("POST", "/vault/devices"));
        Assert.Single(h.Host.Calls("DELETE", "/vault/devices/d0"));
        Assert.Empty(h.Host.Devices);
        // Another port is another origin.
        h.Directory.Pairing = new(0, """{"links":[{"kind":"proxy","pairUrl":"https://shared.example.net:8443/pair#token=t3"}]}""");
        h.Prompts.Picks.Enqueue(["dev"]); h.Prompts.Inputs.Enqueue("Phone");
        Assert.EndsWith(Uri.EscapeDataString("https://shared.example.net:8443/pair#token=t3"), (await h.Hosts.PairPhoneAsync(Slug, default))!.Url.Reveal());
    }

    [Fact]
    public async Task ACredentialThatIsNoUserOfTheHostIsNotSynced()
    {
        await using var h = new Harness();
        h.Directory.Problem = "You are not enrolled on host.example; ask its administrator.";
        await h.Sync();
        Assert.Empty(h.Host.Log); Assert.Equal(h.Directory.Problem, h.State.LastError); Assert.False(h.Vault.HasKey);
        // A host service without the vault routes.
        h.Directory.Problem = null; h.Host.Forced["GET /vault/entries"] = (404, null);
        await h.Sync();
        Assert.Contains("no key vault", h.State.LastError);
        h.Hosts.Online(new("dev", Slug, "dev"), true);
        h.Host.Forced["GET /vault/approvals"] = (404, null);
        await h.Poll(); await h.Poll();
        Assert.Single(h.Host.Calls("GET", "/vault/approvals")); // switched off until a pass succeeds
    }

    [Fact]
    public async Task APhaseOneVaultFileMigratesWithItsTimesAndSyncsAsThisPcsWrites()
    {
        var updated = DateTimeOffset.FromUnixTimeMilliseconds(1720000000123);
        await using var h = new Harness(x =>
        {
            var phase1 = new JsonObject
            {
                ["v"] = 1,
                ["secrets"] = new JsonArray(new JsonObject { ["name"] = "github-token", ["description"] = "CI", ["username"] = "", ["secret"] = "ghp_s3cretTokenValue",
                    ["createdAt"] = 1710000000000, ["updatedAt"] = 1720000000123, ["origin"] = "agent:dev" }),
                ["leases"] = new JsonArray(new JsonObject { ["id"] = "l1", ["instance"] = "dev", ["name"] = "github-token", ["usesLeft"] = 2, ["expiresAt"] = VaultSync.Ms(x.Clock.UtcNow) + 3600000,
                    ["grantedAt"] = 1720000000000, ["reason"] = "r", ["origin"] = "approved" }),
                ["cleanups"] = new JsonArray()
            };
            x.Files.WriteFileAtomic(VaultPath, Encoding.UTF8.GetBytes(Convert.ToBase64String(x.Protection.Protect(Encoding.UTF8.GetBytes(phase1.ToJsonString())))));
        });
        var id = h.Vault.InstallId;
        Assert.Matches("^[0-9a-f]{32}$", id);
        var entry = Assert.Single(h.Vault.Secrets());
        Assert.Equal((updated, "agent:dev"), (entry.UpdatedAt, entry.Origin)); Assert.Single(h.Vault.Leases());
        var reopened = new VaultService(new VaultStore(h.Files, h.Protection, VaultPath), h.Prompts, h.Toasts, h.Clock);
        Assert.Equal(id, reopened.InstallId); Assert.False(reopened.HasKey);
        await h.Sync();
        var pushed = h.Host.Entries["github-token"];
        Assert.Equal((1720000000123L, "pc:" + id), (pushed["updatedAt"]!.GetValue<long>(), pushed["updatedBy"]!.GetValue<string>()));
        Assert.Equal("ghp_s3cretTokenValue", Open(h.Key, pushed));
    }

    [Fact]
    public async Task LocalVmRequestsWriteTombstonesAndRaiseTheSyncSignal()
    {
        await using var h = new Harness();
        var changes = 0; h.Vault.EntriesChanged += () => changes++;
        var add = VaultProtocol.ParseRequest(new JsonObject { ["v"] = 1, ["id"] = "1700000000000-1-3", ["op"] = "add", ["names"] = new JsonArray("deploy-key"), ["description"] = "Deploy",
            ["secret"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("first-value-123")) }.ToJsonString()).Request!;
        Assert.Equal("ok", (await h.Vault.HandleAsync("local-vm", add))["status"]!.GetValue<string>());
        Assert.Equal(1, changes);
        h.Prompts.Approvals.Enqueue(true);
        var delete = VaultProtocol.ParseRequest("""{"v":1,"id":"1700000000000-1-4","op":"delete","names":["deploy-key"]}""").Request!;
        Assert.Equal("ok", (await h.Vault.HandleAsync("local-vm", delete))["status"]!.GetValue<string>());
        Assert.Equal(2, changes);
        await h.Sync();
        var stone = h.Host.Entries["deploy-key"];
        Assert.True(stone["deleted"]!.GetValue<bool>()); Assert.Equal("pc:" + h.Vault.InstallId, stone["updatedBy"]!.GetValue<string>());
        // A later add of the same name beats its own tombstone even within the same millisecond.
        add = VaultProtocol.ParseRequest(new JsonObject { ["v"] = 1, ["id"] = "1700000000000-1-5", ["op"] = "add", ["names"] = new JsonArray("deploy-key"), ["description"] = "Deploy",
            ["secret"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("second-value-456")) }.ToJsonString()).Request!;
        await h.Vault.HandleAsync("local-vm", add);
        await h.Sync();
        Assert.Equal("second-value-456", Open(h.Key, h.Host.Entries["deploy-key"]));
    }

    [Fact]
    public void HostedDefinitionsMapToTheirHostAndVmName()
    {
        Assert.Null(VaultHosts.Instance(new JsonObject { ["name"] = "agent-vm", ["scriptsDir"] = "/x" }));
        var hosted = VaultHosts.Instance(new JsonObject { ["name"] = "remote", ["vmName"] = "dev-01", ["service"] = new JsonObject { ["url"] = "https://Host.Example:7462" } })!;
        Assert.Equal(new VaultHostInstance("remote", Slug, "dev-01"), hosted);
        Assert.Equal("remote", VaultHosts.Instance(new JsonObject { ["name"] = "remote", ["service"] = new JsonObject { ["url"] = "host.example" } })!.VmName);
        Assert.Equal("https://h:7462/vault/pair#token=abc", VaultSync.PairingUrl("https://h:7462/", "abc", null));
    }

    [Fact]
    public async Task RuntimesReportOnlineTransitionsOnce()
    {
        var clock = new FakeClock(); var probe = new FakeRuntimeProbe(); var seen = new List<bool>();
        await using (var runtime = new InstanceRuntime(new("dev", "1", ForwardsEnabled: false, NotificationsEnabled: false), probe, clock,
            _ => throw new InvalidOperationException(), () => throw new InvalidOperationException(), _ => throw new InvalidOperationException(), new RepatchJob(new FakeSshTransport()), new RuntimeMessageBus(),
            onlineChanged: seen.Add))
        {
            await runtime.ProbeOnceAsync(); await runtime.ProbeOnceAsync();
            probe.State = new() { ["online"] = false }; await runtime.ProbeOnceAsync(); await runtime.ProbeOnceAsync();
            probe.State = new() { ["online"] = true }; await runtime.ProbeOnceAsync();
            Assert.Equal([true, false, true], seen);
        }
        Assert.Equal([true, false, true, false], seen);
    }
}
