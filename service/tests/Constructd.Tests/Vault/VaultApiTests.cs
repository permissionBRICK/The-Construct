using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Tests.Support;

namespace Constructd.Tests.Vault;

/// <summary>Sync, settings, unlock and the auth boundaries of every vault route.</summary>
public sealed class VaultApiTests
{
    [Fact]
    public async Task Sync_applies_last_writer_wins_keeps_tombstones_and_counts_revisions()
    {
        using var h = await VaultHarness.CreateAsync();
        var first = await h.PutEntriesAsync(h.Entry("github-token", "ghp_one", description: "GitHub", at: 1000, by: "pc:a"), h.Entry("db", "pw-123456", "admin", at: 1000));
        Assert.Equal(1, first["revision"]!.GetValue<long>());
        Assert.Equal(2, first["applied"]!.GetValue<int>());

        // Older loses, an identical stamp is no change, a tie on updatedAt goes to the larger updatedBy.
        var older = await h.PutEntriesAsync(h.Entry("github-token", "ghp_old", at: 999, by: "pc:z"), h.Entry("github-token", "ghp_same", at: 1000, by: "pc:a"));
        Assert.Equal(0, older["applied"]!.GetValue<int>());
        Assert.Equal(1, older["revision"]!.GetValue<long>());
        var tie = await h.PutEntriesAsync(h.Entry("github-token", "ghp_tie", description: "tie", at: 1000, by: "pc:b"));
        Assert.Equal(1, tie["applied"]!.GetValue<int>());
        Assert.Equal(2, tie["revision"]!.GetValue<long>());

        var tombstone = await h.PutEntriesAsync(new { name = "db", description = "", hasUsername = false, payload = (string?)null, updatedAt = 2000L, updatedBy = "pc:a", deleted = true });
        Assert.Equal(3, tombstone["revision"]!.GetValue<long>());

        var all = await h.EntriesAsync();
        Assert.Equal(3, all["revision"]!.GetValue<long>());
        Assert.Equal("available", all["mode"]!.GetValue<string>());
        Assert.True(VaultCrypto.Verify(h.Key, all["keyCheck"]!.GetValue<string>()));
        var entries = all["entries"]!.AsArray();
        var db = entries.Single(e => e!["name"]!.GetValue<string>() == "db")!;
        Assert.True(db["deleted"]!.GetValue<bool>());
        Assert.Null(db["payload"]);
        var token = entries.Single(e => e!["name"]!.GetValue<string>() == "github-token")!;
        Assert.Equal("pc:b", token["updatedBy"]!.GetValue<string>());
        Assert.Equal(("", "ghp_tie"), VaultCrypto.OpenEntry(h.Key, "github-token", 1000, token["payload"]!.GetValue<string>()));
        Assert.Empty(all["unlockedVms"]!.AsArray());
        Assert.NotNull(all["devices"]);

        // Tombstones live 30 days, then the tick prunes them.
        h.App.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(2000).AddDays(31);
        await h.Vault.TickAsync(default);
        Assert.DoesNotContain((await h.EntriesAsync())["entries"]!.AsArray(), e => e!["name"]!.GetValue<string>() == "db");
    }

    [Theory]
    [InlineData("""{"entries":[{"name":"../x","payload":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","updatedAt":1,"updatedBy":"pc:a"}]}""", "entries[0].name")]
    [InlineData("""{"entries":[{"name":"x","payload":"AAAA","updatedAt":1,"updatedBy":"pc:a"}]}""", "entries[0].payload")]
    [InlineData("""{"entries":[{"name":"x","updatedAt":1,"updatedBy":"pc:a"}]}""", "entries[0].payload")]
    [InlineData("""{"entries":[{"name":"x","payload":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","updatedAt":0,"updatedBy":"pc:a"}]}""", "entries[0].updatedAt")]
    [InlineData("""{"entries":[{"name":"x","payload":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","updatedAt":1,"updatedBy":""}]}""", "entries[0].updatedBy")]
    [InlineData("""{}""", "entries")]
    public async Task Invalid_entries_are_refused_without_a_partial_write(string body, string field)
    {
        using var h = await VaultHarness.CreateAsync();
        using var response = await h.Bob.PutAsync("/api/v1/vault/entries", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(field, (await response.Content.ReadFromJsonAsync<JsonObject>())!["field"]!.GetValue<string>());
        Assert.Equal(0, (await h.EntriesAsync())["revision"]!.GetValue<long>());
    }

    [Fact]
    public async Task Settings_set_the_key_check_once_and_refuse_a_wrong_key()
    {
        using var h = await VaultHarness.CreateAsync(mode: null);
        var fresh = await h.EntriesAsync();
        Assert.Null(fresh["mode"]);
        Assert.Null(fresh["keyCheck"]);

        // The first call must carry the key check; available mode needs the key.
        using (var noCheck = await h.Bob.PutAsJsonAsync("/api/v1/vault/settings", new { mode = "locked" }))
            Assert.Equal(HttpStatusCode.BadRequest, noCheck.StatusCode);
        using (var noKey = await h.Bob.PutAsJsonAsync("/api/v1/vault/settings", new { mode = "available", keyCheck = VaultCrypto.KeyCheck(h.Key) }))
            Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        using (var wrong = await h.Bob.PutAsJsonAsync("/api/v1/vault/settings", new { mode = "available", key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), keyCheck = VaultCrypto.KeyCheck(h.Key) }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.Equal("wrong-key", (await wrong.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        }

        await h.SettingsAsync("locked");
        var locked = await h.EntriesAsync();
        Assert.Equal("locked", locked["mode"]!.GetValue<string>());

        // The first key check stays: another key (with its own check) is refused, a re-sealed check is ignored.
        var other = RandomNumberGenerator.GetBytes(32);
        using (var mismatch = await h.Bob.PutAsJsonAsync("/api/v1/vault/settings", new { mode = "available", key = Convert.ToBase64String(other), keyCheck = VaultCrypto.KeyCheck(other) }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
            Assert.Equal("wrong-key", (await mismatch.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        }
        using (var available = await h.Bob.PutAsJsonAsync("/api/v1/vault/settings", new { mode = "available", key = h.KeyBase64, keyCheck = VaultCrypto.KeyCheck(h.Key) }))
            Assert.Equal(HttpStatusCode.NoContent, available.StatusCode);
        var after = await h.EntriesAsync();
        Assert.Equal("available", after["mode"]!.GetValue<string>());
        Assert.Equal(locked["keyCheck"]!.GetValue<string>(), after["keyCheck"]!.GetValue<string>());

        // The wrapped key exists only in available mode.
        Assert.NotNull((await h.App.Service<IVaultStore>().GetOwnerAsync("bob", default))!.WrappedKey);
        await h.SettingsAsync("locked");
        Assert.Null((await h.App.Service<IVaultStore>().GetOwnerAsync("bob", default))!.WrappedKey);
    }

    [Fact]
    public async Task Unlock_checks_the_key_and_power_stop_drops_it()
    {
        using var h = await VaultHarness.CreateAsync("locked");
        await h.PutEntriesAsync(h.Entry("db", "pw-123456"));
        await h.ApprovedAsync(h.Request("request", ["db"]));

        var locked = await h.AnswerAsync(h.Request("get", ["db"]));
        Assert.Equal("locked", locked["status"]!.GetValue<string>());
        Assert.Equal("The key vault is locked for this VM: start or connect it from your PC's Construct Companion.", locked["message"]!.GetValue<string>());

        using (var wrong = await h.Bob.PostAsJsonAsync("/api/v1/vms/dev/vault/unlock", new { key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.Equal("wrong-key", (await wrong.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        }
        using (var unlock = await h.Bob.PostAsJsonAsync("/api/v1/vms/dev/vault/unlock", new { key = h.KeyBase64 }))
            Assert.Equal(HttpStatusCode.NoContent, unlock.StatusCode);
        using (var again = await h.Bob.PostAsJsonAsync("/api/v1/vms/dev/vault/unlock", new { key = h.KeyBase64 }))
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(["dev"], (await h.EntriesAsync())["unlockedVms"]!.AsArray().Select(n => n!.GetValue<string>()));

        var value = await h.AnswerAsync(h.Request("get", ["db"]));
        Assert.Equal("pw-123456", VaultHarness.Secret(value));

        using (var stop = await h.Bob.PostAsJsonAsync("/api/v1/vms/dev/power", new { action = "stop" }))
            Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        Assert.False(h.Vault.IsUnlocked("dev"));
        Assert.Equal("locked", (await h.AnswerAsync(h.Request("get", ["db"])))["status"]!.GetValue<string>());

        // Only a running VM can be unlocked.
        using (var off = await h.Bob.PostAsJsonAsync("/api/v1/vms/dev/vault/unlock", new { key = h.KeyBase64 }))
        {
            Assert.Equal(HttpStatusCode.Conflict, off.StatusCode);
            Assert.Equal("vm-not-running", (await off.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Save_idle_state_changes_and_deletion_drop_the_unlocked_key()
    {
        using var h = await VaultHarness.CreateAsync("locked");
        async Task Unlock()
        {
            h.App.Driver.SetState("dev", VmState.Running);
            using var unlock = await h.Bob.PostAsJsonAsync("/api/v1/vms/dev/vault/unlock", new { key = h.KeyBase64 });
            Assert.Equal(HttpStatusCode.NoContent, unlock.StatusCode);
            Assert.True(h.Vault.IsUnlocked("dev"));
        }

        await Unlock();
        using (var save = await h.Bob.PostAsJsonAsync("/api/v1/vms/dev/power", new { action = "save" }))
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        Assert.False(h.Vault.IsUnlocked("dev"));

        // Observed in any other state (the 30 s check).
        await Unlock();
        h.App.Driver.SetState("dev", VmState.Paused);
        await h.Vault.CheckUnlockedAsync(default);
        Assert.False(h.Vault.IsUnlocked("dev"));

        // The idle engine observing a non-running VM.
        await Unlock();
        h.App.Driver.SetState("dev", VmState.Off);
        await h.App.Service<IIdlePolicyEngine>().EvaluateAsync(h.App.Clock.UtcNow, default);
        Assert.False(h.Vault.IsUnlocked("dev"));

        // The deletion fence.
        await Unlock();
        using (var delete = await h.Bob.DeleteAsync("/api/v1/vms/dev"))
            Assert.Equal(HttpStatusCode.Accepted, delete.StatusCode);
        Assert.False(h.Vault.IsUnlocked("dev"));
    }

    [Fact]
    public async Task Always_available_needs_no_unlock_and_an_unset_vault_answers_locked()
    {
        using var unset = await VaultHarness.CreateAsync(mode: null);
        var list = await unset.AnswerAsync(unset.Request("list", []));
        Assert.Equal("ok", list["status"]!.GetValue<string>());
        foreach (var op in new[] { "request", "get", "delete" })
        {
            var refused = await unset.AnswerAsync(unset.Request(op, ["x"]));
            Assert.Equal("locked", refused["status"]!.GetValue<string>());
            Assert.Contains("open the Key Vault in your PC's Construct Companion", refused["message"]!.GetValue<string>());
        }
        // The Companion unlocks after every start: before the vault is set up that is a no-op.
        using (var early = await unset.Bob.PostAsJsonAsync("/api/v1/vms/dev/vault/unlock", new { key = unset.KeyBase64 }))
            Assert.Equal(HttpStatusCode.NoContent, early.StatusCode);
        Assert.False(unset.Vault.IsUnlocked("dev"));

        using var h = await VaultHarness.CreateAsync("available");
        await h.PutEntriesAsync(h.Entry("db", "pw-123456"));
        Assert.False(h.Vault.IsUnlocked("dev"));
        var value = await h.ApprovedAsync(h.Request("get", ["db"]));
        Assert.Equal("pw-123456", VaultHarness.Secret(value));
        // Unlock is a no-op in available mode, whatever the key.
        using var noop = await h.Bob.PostAsJsonAsync("/api/v1/vms/dev/vault/unlock", new { key = "not-a-key" });
        Assert.Equal(HttpStatusCode.NoContent, noop.StatusCode);
    }

    [Fact]
    public async Task Other_users_and_admins_never_reach_someone_elses_vault()
    {
        using var h = await VaultHarness.CreateAsync();
        await h.PutEntriesAsync(h.Entry("db", "pw-123456"));
        using var carol = await h.App.CreateUserClientAsync("carol");
        using var admin = await h.App.CreateUserClientAsync("root-admin", Role.Admin);
        await h.ApprovedAsync(h.Request("request", ["db"]));
        var pending = await h.PendingAsync(h.Request("delete", ["db"]));
        var lease = (await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/leases"))!["leases"]!.AsArray().Single()!["id"]!.GetValue<string>();

        foreach (var other in new[] { carol, admin })
        {
            // Their own (empty) vault, never bob's.
            var entries = (await other.GetFromJsonAsync<JsonObject>("/api/v1/vault/entries"))!;
            Assert.Empty(entries["entries"]!.AsArray());
            Assert.Empty((await other.GetFromJsonAsync<JsonObject>("/api/v1/vault/approvals"))!["approvals"]!.AsArray());
            Assert.Empty((await other.GetFromJsonAsync<JsonObject>("/api/v1/vault/leases"))!["leases"]!.AsArray());
            Assert.Equal(HttpStatusCode.NotFound, await h.DecideAsync(pending, "approve", other));
            using (var revoke = await other.DeleteAsync($"/api/v1/vault/leases/{lease}"))
                Assert.Equal(HttpStatusCode.NotFound, revoke.StatusCode);
            using (var unlock = await other.PostAsJsonAsync("/api/v1/vms/dev/vault/unlock", new { key = h.KeyBase64 }))
            {
                Assert.Equal(HttpStatusCode.Forbidden, unlock.StatusCode);
                Assert.Equal("not-owner", (await unlock.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
            }
            // A user credential is not a guest: no requests on bob's VM, not even by the owner.
            Assert.Equal(HttpStatusCode.Forbidden, (await h.AskAsync(h.Request("list", []), other)).Status);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await h.AskAsync(h.Request("list", []), h.Bob)).Status);
        Assert.Single((await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/approvals"))!["approvals"]!.AsArray());
    }

    [Fact]
    public async Task A_vm_token_reaches_only_its_own_primary_vm_and_no_user_route()
    {
        using var h = await VaultHarness.CreateAsync();
        await h.App.Vms.AddAsync(new("other", "bob", 1, 1, 1, h.App.Clock.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, [], TokenKind: VmTokenKind.Primary), 5, default);
        using var other = h.App.CreateVmTokenClient(await h.App.Service<IVmTokenIssuer>().IssueVmTokenAsync("other", VmTokenKind.Primary, default));
        Assert.Equal(HttpStatusCode.Forbidden, (await h.AskAsync(h.Request("list", []), other)).Status);
        using (var scrubs = await other.GetAsync("/api/v1/vms/dev/vault/scrubs"))
            Assert.Equal(HttpStatusCode.Forbidden, scrubs.StatusCode);

        // The VM's own token is refused on every user route.
        foreach (var path in new[] { "/api/v1/vault/entries", "/api/v1/vault/approvals", "/api/v1/vault/leases", "/api/v1/vault/files", "/api/v1/vault/devices", "/api/v1/vault/activity", "/api/v1/vault/device" })
        {
            using var response = await h.Guest.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, path + ": " + response.StatusCode);
        }
        using (var unlock = await h.Guest.PostAsJsonAsync("/api/v1/vms/dev/vault/unlock", new { key = h.KeyBase64 }))
            Assert.Equal(HttpStatusCode.Forbidden, unlock.StatusCode);
        using (var pair = await h.Guest.PostAsJsonAsync("/api/v1/vault/devices", new { label = "agent" }))
            Assert.Equal(HttpStatusCode.Forbidden, pair.StatusCode);
    }

    [Fact]
    public async Task Child_vms_have_no_vault()
    {
        using var h = await VaultHarness.CreateAsync();
        await h.App.Vms.AddAsync(new("dev-child", "bob", 1, 1, 1, h.App.Clock.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, [],
            Kind: VmKind.Child, Parent: "dev"), 5, default);
        h.App.Driver.SetState("dev-child", VmState.Running);

        var (status, body) = await h.AskAsync(h.Request("list", []), vm: "dev-child");
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("vault-child", body["code"]!.GetValue<string>());
        using (var scrubs = await h.Guest.GetAsync("/api/v1/vms/dev-child/vault/scrubs"))
            Assert.Equal(HttpStatusCode.Forbidden, scrubs.StatusCode);
        using (var unlock = await h.Bob.PostAsJsonAsync("/api/v1/vms/dev-child/vault/unlock", new { key = h.KeyBase64 }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, unlock.StatusCode);
            Assert.Equal("vault-child", (await unlock.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task A_device_answers_approvals_and_files_only_and_dies_with_its_revocation()
    {
        using var h = await VaultHarness.CreateAsync();
        await h.PutEntriesAsync(h.Entry("db", "pw-123456", description: "Staging DB"));

        using var pair = await h.Bob.PostAsJsonAsync("/api/v1/vault/devices", new { label = "Phone" });
        Assert.Equal(HttpStatusCode.OK, pair.StatusCode);
        var paired = (await pair.Content.ReadFromJsonAsync<JsonObject>())!;
        var token = paired["token"]!.GetValue<string>();
        Assert.Equal(43, token.Length);
        Assert.Equal("https://buildbox.test:7462", paired["webUrl"]!.GetValue<string>());
        var devices = (await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/devices"))!;
        Assert.Equal("https://buildbox.test:7462", devices["webUrl"]!.GetValue<string>());
        Assert.Equal("Phone", devices["devices"]!.AsArray().Single()!["label"]!.GetValue<string>());
        Assert.DoesNotContain(token, devices.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(TokenHasher.Hash(token), devices.ToJsonString(), StringComparison.Ordinal);

        using var phone = h.DeviceClient(token);
        var me = (await phone.GetFromJsonAsync<JsonObject>("/api/v1/vault/device"))!;
        Assert.Equal("bob", me["user"]!.GetValue<string>());
        Assert.Equal("Phone", me["label"]!.GetValue<string>());

        var id = await h.PendingAsync(h.Request("request", ["db"], new { reason = "deploy" }));
        var approvals = (await phone.GetFromJsonAsync<JsonObject>("/api/v1/vault/approvals"))!["approvals"]!.AsArray();
        var approval = approvals.Single()!;
        Assert.Equal(id, approval["id"]!.GetValue<string>());
        Assert.Equal("dev", approval["vm"]!.GetValue<string>());
        Assert.Equal("request", approval["op"]!.GetValue<string>());
        Assert.Contains("db — Staging DB", approval["message"]!.GetValue<string>());
        Assert.Equal(3600, approval["ttlSeconds"]!.GetValue<int>());
        Assert.Equal("root@dev", approval["source"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, await h.DecideAsync(id, "approve", phone));
        Assert.Equal(HttpStatusCode.OK, (await h.PollAsync(id)).Status);
        using (var files = await phone.GetAsync("/api/v1/vault/files"))
            Assert.Equal(HttpStatusCode.OK, files.StatusCode);

        // Nothing else: no values, leases, sync, pairing, identity or VM routes.
        foreach (var (method, path) in new[] { ("GET", "/api/v1/vault/entries"), ("GET", "/api/v1/vault/leases"), ("GET", "/api/v1/vault/devices"),
                     ("POST", "/api/v1/vault/devices"), ("GET", "/api/v1/vault/activity"), ("PUT", "/api/v1/vault/settings"),
                     ("GET", "/api/v1/whoami"), ("GET", "/api/v1/vms"), ("POST", "/api/v1/vms/dev/vault/requests"), ("GET", "/api/v1/vms/dev/vault/scrubs"),
                     ("POST", "/api/v1/vms/dev/vault/unlock") })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { label = "x", key = h.KeyBase64 }) };
            using var response = await phone.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path}: {response.StatusCode}");
        }

        var deviceId = paired["id"]!.GetValue<string>();
        using (var revoke = await h.Bob.DeleteAsync($"/api/v1/vault/devices/{deviceId}"))
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using (var after = await phone.GetAsync("/api/v1/vault/approvals"))
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        using (var unknown = await h.Bob.DeleteAsync($"/api/v1/vault/devices/{deviceId}"))
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task The_vault_web_url_setting_overrides_the_default()
    {
        using var app = new TestApp(new Dictionary<string, string?> { ["Constructd:VaultWebUrl"] = "https://vault.example.test/base/" });
        using var h = await VaultHarness.CreateAsync(app: app);
        using var pair = await h.Bob.PostAsJsonAsync("/api/v1/vault/devices", new { label = "Phone" });
        var paired = (await pair.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("https://vault.example.test/base", paired["webUrl"]!.GetValue<string>());
        Assert.Equal("option", paired["webUrlSource"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_host_setting_wins_over_the_option_and_the_default_for_pairing_and_the_banner()
    {
        using var app = new TestApp(new Dictionary<string, string?> { ["Constructd:VaultWebUrl"] = "https://vault.example.test/base/" });
        using var h = await VaultHarness.CreateAsync(app: app);
        await h.PutEntriesAsync(h.Entry("db", "pw-123456"));
        using var admin = await app.CreateUserClientAsync("root-admin", Role.Admin);
        var config = (await admin.GetFromJsonAsync<JsonObject>("/api/v1/host/config"))!;
        Assert.Equal("default", config["vault"]!["source"]!.GetValue<string>());
        Assert.Null(config["vault"]!["webUrl"]);
        using (var put = await admin.PutAsJsonAsync("/api/v1/host/config", new { vault = new { webUrl = "https://vault.example.net:8443/" } }))
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var pair = await h.Bob.PostAsJsonAsync("/api/v1/vault/devices", new { label = "Phone" });
        var paired = (await pair.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(("https://vault.example.net:8443", "hostConfig"), (paired["webUrl"]!.GetValue<string>(), paired["webUrlSource"]!.GetValue<string>()));
        var devices = (await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/devices"))!;
        Assert.Equal(("https://vault.example.net:8443", "hostConfig"), (devices["webUrl"]!.GetValue<string>(), devices["webUrlSource"]!.GetValue<string>()));
        var (status, body) = await h.AskAsync(h.Request("request", ["db"], new { reason = "deploy" }));
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("https://vault.example.net:8443/vault/#request=" + body["id"]!.GetValue<string>(), body["approveUrl"]!.GetValue<string>());

        // Back to null: the service option applies again.
        using (var put = await admin.PutAsJsonAsync("/api/v1/host/config", new { vault = new { webUrl = (string?)null } }))
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        devices = (await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/devices"))!;
        Assert.Equal(("https://vault.example.test/base", "option"), (devices["webUrl"]!.GetValue<string>(), devices["webUrlSource"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Without_any_setting_phones_get_the_service_address_and_the_reply_says_so()
    {
        using var h = await VaultHarness.CreateAsync();
        var devices = (await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/devices"))!;
        Assert.Equal(("https://buildbox.test:7462", "default"), (devices["webUrl"]!.GetValue<string>(), devices["webUrlSource"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Only_host_admins_change_the_approval_page_address()
    {
        using var h = await VaultHarness.CreateAsync();
        foreach (var client in new[] { h.Bob, h.Guest })
        {
            using var put = await client.PutAsJsonAsync("/api/v1/host/config", new { vault = new { webUrl = "https://evil.example.net" } });
            Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        }
        var devices = (await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/devices"))!;
        Assert.Equal("default", devices["webUrlSource"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("http://vault.example.net")]
    [InlineData("https://vault.example.net/vault")]
    [InlineData("https://vault.example.net/?a=1")]
    [InlineData("https://vault.example.net#x")]
    [InlineData("https://user@vault.example.net")]
    [InlineData("https://vault.example.net:0")]
    [InlineData("https://vault.example.net:65536")]
    [InlineData("vault.example.net")]
    [InlineData("ftp://vault.example.net")]
    [InlineData("https://vault example.net")]
    [InlineData("https://vault.example.net//")]
    public async Task An_approval_page_address_with_a_path_or_without_https_is_refused(string webUrl)
    {
        Assert.Equal(VaultWebAddress.Rule, HostConfigValidation.Validate(new VaultConfig(webUrl)));
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("root-admin", Role.Admin);
        using var put = await admin.PutAsJsonAsync("/api/v1/host/config", new { vault = new { webUrl } });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal("vault", (await put.Content.ReadFromJsonAsync<JsonObject>())!["field"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://vault.example.net")]
    [InlineData("https://vault.example.net:8443/")]
    [InlineData("https://[2001:db8::5]:8443")]
    [InlineData("http://localhost:7000")]
    [InlineData("http://127.0.0.1:7000")]
    [InlineData("http://[::1]:7000")]
    public void A_bare_https_origin_or_loopback_http_is_accepted(string? webUrl) =>
        Assert.Null(HostConfigValidation.Validate(new VaultConfig(webUrl)));

    [Fact]
    public async Task Audit_and_logs_never_carry_values_keys_or_tokens()
    {
        using var h = await VaultHarness.CreateAsync("locked");
        using var admin = await h.App.CreateUserClientAsync("root-admin", Role.Admin);
        await h.PutEntriesAsync(h.Entry("db", "pw-super-secret-123"));
        using (var unlock = await h.Bob.PostAsJsonAsync("/api/v1/vms/dev/vault/unlock", new { key = h.KeyBase64 }))
            Assert.Equal(HttpStatusCode.NoContent, unlock.StatusCode);
        var value = await h.ApprovedAsync(h.Request("get", ["db"]));
        Assert.Equal("pw-super-secret-123", VaultHarness.Secret(value));
        await h.AnswerAsync(h.Request("add", ["new-token"], new { secret = Convert.ToBase64String("agent-made-secret-456"u8.ToArray()), description = "made by the agent" }));
        using var pair = await h.Bob.PostAsJsonAsync("/api/v1/vault/devices", new { label = "Phone" });
        var deviceToken = (await pair.Content.ReadFromJsonAsync<JsonObject>())!["token"]!.GetValue<string>();

        var audit = await (await admin.GetAsync("/api/v1/audit?limit=1000")).ReadAsync<List<AuditResponse>>();
        var text = string.Join("\n", audit.Select(e => $"{e.Actor} {e.Action} {e.Target} {e.Detail}"));
        Assert.Contains("vault.request", text);
        Assert.Contains("vault.unlock", text);
        Assert.Contains("vault.approval", text);
        var logs = h.App.Logs.AllText();
        foreach (var secret in new[] { "pw-super-secret-123", "agent-made-secret-456", h.KeyBase64, deviceToken, h.GuestToken })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
        }
    }
}
