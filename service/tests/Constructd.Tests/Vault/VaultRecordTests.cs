using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Construct.Keys;
using Constructd.Core.Logic;
using Constructd.Store.Records;
using Constructd.Sync.Vault;
using Constructd.Tests.Support;

namespace Constructd.Tests.Vault;

/// <summary>
/// The hosted vault of an account in the new stack (plan 9.1, 9.6, 8.2, 10.3): secrets as records, the engine
/// writes the 9.1 table permits with their provenance, stale bases re-applied, leases ended by a device's change,
/// secret entry on request, the two vault modes, and the old-style vault next to it.
/// </summary>
public sealed class VaultRecordTests
{
    // ---- the 9.1 table, row by row ----

    [Fact]
    public async Task Row_1_a_VM_adds_a_new_name_without_approval_recorded_as_that_VM()
    {
        using var h = await RecordVaultHarness.CreateAsync();
        var answer = await h.AnswerAsync(h.Add("npm-read", "npm_s3cr3t_value", "Read-only npm token"));
        Assert.Equal("ok", answer["status"]!.GetValue<string>());

        var stored = (await h.StoredAsync("npm-read"))!;
        var record = SecretRecords.Read(h.Keys.Records, stored)!;
        Assert.Equal(RecordKinds.Secret, stored.Kind);
        Assert.Equal("engine:" + h.EngineId, stored.Actor);
        Assert.Equal(SecretProvenance.VmAdded, record.Value.Provenance.Kind);
        Assert.Equal("dev", record.Value.Provenance.Vm);
        Assert.Equal(h.EngineId, record.Value.Provenance.Engine);
        Assert.Null(record.Value.Provenance.Device);
        Assert.False(string.IsNullOrEmpty(record.Value.Provenance.RequestId));
        Assert.Contains("vm:dev", Constructd.Sync.Sealing.RecordPayload.OriginsOf(record.Payload));
        Assert.Equal("Read-only npm token", record.Value.Description);
        // Today's entry shape: the value opens with the account's K exactly as the Companion sealed it.
        Assert.Equal(("", "npm_s3cr3t_value", false), await h.OpenAsync("npm-read"));

        // An existing name is never overwritten without a device.
        var (status, again) = await h.AskAsync(h.Add("npm-read", "other"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("exists", again["status"]!.GetValue<string>());
        Assert.Equal(stored.Revision, (await h.StoredAsync("npm-read"))!.Revision);
    }

    [Fact]
    public async Task Row_2_a_VM_turns_auto_approve_off_without_approval()
    {
        using var h = await RecordVaultHarness.CreateAsync();
        await h.DeviceWriteAsync("ci-token", "ci_value_123", autoApprove: true);
        var answer = await h.AnswerAsync(h.Request("autoApprove", ["ci-token"], new { autoApprove = false }));
        Assert.Equal("ok", answer["status"]!.GetValue<string>());
        Assert.False(answer["autoApprove"]!.GetValue<bool>());

        var record = (await h.ReadAsync("ci-token"))!;
        Assert.Equal(SecretProvenance.VmAutoApproveOff, record.Value.Provenance.Kind);
        Assert.Equal("dev", record.Value.Provenance.Vm);
        Assert.Null(record.Value.Provenance.Device);
        Assert.False(record.Value.AutoApprove);
        // Re-sealed: the flag a device trusts says the same, and the value is unchanged.
        Assert.Equal(("", "ci_value_123", false), await h.OpenAsync("ci-token"));
    }

    [Fact]
    public async Task Row_3_replace_delete_and_auto_approve_on_wait_for_a_device_and_record_it()
    {
        using var h = await RecordVaultHarness.CreateAsync();
        await h.DeviceWriteAsync("deploy", "deploy_old_value");

        // Replace: pending until the laptop approves, then recorded with VM, device and request id.
        var replaceId = await h.PendingAsync(h.Add("deploy", "deploy_new_value") .Also(r => r["replace"] = true));
        Assert.Equal(("", "deploy_old_value", false), await h.OpenAsync("deploy"));
        using (var device = h.Device())
            Assert.Equal(HttpStatusCode.NoContent, (await device.PostAsJsonAsync($"vault/approvals/{replaceId}", new { decision = "approve" })).StatusCode);
        Assert.Equal("ok", (await h.PollAsync(replaceId))["status"]!.GetValue<string>());
        var replaced = (await h.ReadAsync("deploy"))!;
        Assert.Equal(new SecretProvenance(SecretProvenance.VmApproved, "dev", h.EngineId, h.Laptop.Actor, replaceId), replaced.Value.Provenance);
        Assert.Equal(("", "deploy_new_value", false), await h.OpenAsync("deploy"));

        // Auto-approve on.
        var (onId, on) = await h.ApprovedAsync(h.Request("autoApprove", ["deploy"], new { autoApprove = true }));
        Assert.True(on["autoApprove"]!.GetValue<bool>());
        var flagged = (await h.ReadAsync("deploy"))!;
        Assert.Equal(new SecretProvenance(SecretProvenance.VmApproved, "dev", h.EngineId, h.Laptop.Actor, onId), flagged.Value.Provenance);
        Assert.True((await h.OpenAsync("deploy")).AutoApprove);

        // Delete: a tombstone that still carries who caused it.
        var (deleteId, deleted) = await h.ApprovedAsync(h.Request("delete", ["deploy"]));
        Assert.Equal("ok", deleted["status"]!.GetValue<string>());
        var tombstone = (await h.StoredAsync("deploy"))!;
        Assert.True(tombstone.Deleted);
        var read = SecretRecords.Read(h.Keys.Records, tombstone)!;
        Assert.Null(read.Value.Payload);
        Assert.Equal(new SecretProvenance(SecretProvenance.VmApproved, "dev", h.EngineId, h.Laptop.Actor, deleteId), read.Value.Provenance);
    }

    [Fact]
    public async Task Row_3_a_denial_writes_nothing_and_a_VM_cannot_answer_its_own_request()
    {
        using var h = await RecordVaultHarness.CreateAsync();
        var original = await h.DeviceWriteAsync("deploy", "deploy_old_value");
        var id = await h.PendingAsync(h.Request("delete", ["deploy"]));
        using (var self = await h.Guest.PostAsJsonAsync($"/api/v1/vault/approvals/{id}", new { decision = "approve" }))
            Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        using (var device = h.Device())
            Assert.Equal(HttpStatusCode.NoContent, (await device.PostAsJsonAsync($"vault/approvals/{id}", new { decision = "deny" })).StatusCode);
        Assert.Equal("denied", (await h.PollAsync(id))["status"]!.GetValue<string>());
        Assert.Equal(original.Revision, (await h.StoredAsync("deploy"))!.Revision);
    }

    [Fact]
    public async Task Nothing_else_the_Companion_routes_do_not_write_records()
    {
        using var h = await RecordVaultHarness.CreateAsync();
        var payload = VaultCrypto.SealEntry(h.Keys.Vault, "x", 1000, "", "value_x");
        using (var put = await h.Bob.PutAsJsonAsync("/api/v1/vault/entries", new { entries = new[] { new { name = "x", description = "", hasUsername = false, payload, updatedAt = 1000L, updatedBy = "pc:a", deleted = false } } }))
        {
            Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
            Assert.Equal("vault-records", (await put.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        }

        using (var settings = await h.Bob.PutAsJsonAsync("/api/v1/vault/settings", new { mode = "locked", keyCheck = VaultCrypto.KeyCheck(h.Keys.Vault) }))
            Assert.Equal(HttpStatusCode.Conflict, settings.StatusCode);
        Assert.Null(await h.StoredAsync("x"));
        Assert.Equal(0, h.Racing.Writes);
    }

    // ---- 9.6: no merge; a stale base is re-read and the one change applied again ----

    [Fact]
    public async Task A_stale_base_is_re_read_and_the_one_change_applied_again()
    {
        using var h = await RecordVaultHarness.CreateAsync();
        await h.DeviceWriteAsync("ci-token", "ci_value_123", "first description", autoApprove: true);

        // The laptop changes the description just before the engine writes: the engine's base is stale.
        h.Racing.BeforeNextWrite = async () => await h.DeviceWriteAsync("ci-token", "ci_value_123", "second description", autoApprove: true);
        var answer = await h.AnswerAsync(h.Request("autoApprove", ["ci-token"], new { autoApprove = false }));
        Assert.Equal("ok", answer["status"]!.GetValue<string>());
        Assert.Equal(1, h.Racing.Stale);
        Assert.Equal(2, h.Racing.Writes);

        // Both changes hold: the laptop's description and the VM's switch.
        var record = (await h.ReadAsync("ci-token"))!;
        Assert.Equal("second description", record.Value.Description);
        Assert.False(record.Value.AutoApprove);
        Assert.False((await h.OpenAsync("ci-token")).AutoApprove);
        Assert.Equal(SecretProvenance.VmAutoApproveOff, record.Value.Provenance.Kind);
    }

    [Fact]
    public async Task A_VM_add_that_finds_the_name_taken_on_its_re_read_overwrites_nothing()
    {
        using var h = await RecordVaultHarness.CreateAsync();
        h.Racing.BeforeNextWrite = async () => await h.DeviceWriteAsync("fresh", "laptop_value_1");
        var answer = await h.AnswerAsync(h.Add("fresh", "vm_value_222"));
        Assert.Equal("exists", answer["status"]!.GetValue<string>());
        Assert.Equal(("", "laptop_value_1", false), await h.OpenAsync("fresh"));
        Assert.Equal(SecretProvenance.ByDevice, (await h.ReadAsync("fresh"))!.Value.Provenance.Kind);
    }

    // ---- leases end when a device changes a value ----

    [Fact]
    public async Task A_value_changed_on_a_device_ends_the_leases_on_it_and_queues_the_scrub()
    {
        using var h = await RecordVaultHarness.CreateAsync();
        await h.DeviceWriteAsync("db", "db_password_1");
        await h.ApprovedAsync(h.Request("request", ["db"], new { ttl = 3600 }));
        Assert.Single(await h.LeasesAsync());

        // A new description only: the value is the same, the lease stays.
        await h.DeviceWriteAsync("db", "db_password_1", "the database, renamed");
        await h.Vault.TickAsync(default);
        Assert.Single(await h.LeasesAsync());
        var got = await h.AnswerAsync(h.Request("get", ["db"]));
        Assert.Equal("db_password_1", Encoding.UTF8.GetString(Convert.FromBase64String(got["secret"]!.GetValue<string>())));

        // A new value: the lease ends and the VM is scrubbed of the old one.
        await h.DeviceWriteAsync("db", "db_password_2");
        await h.Vault.TickAsync(default);
        Assert.Empty(await h.LeasesAsync());
        Assert.Contains("Access to db ended: its value changed; scrubbing the VM.", await h.ActivityAsync());
        h.App.Clock.Advance(TimeSpan.FromSeconds(10));
        var vm = (await h.App.Vms.GetAsync("dev", default))!;
        Assert.True(await h.Vault.ScrubDueAsync(vm, default));

        // The next get of the new value asks again.
        Assert.Equal(HttpStatusCode.Accepted, (await h.AskAsync(h.Request("get", ["db"]))).Status);
    }

    [Fact]
    public async Task An_approved_one_time_get_does_not_hand_out_a_value_replaced_before_pickup()
    {
        using var h = await RecordVaultHarness.CreateAsync();
        await h.DeviceWriteAsync("db", "db_password_A1");
        var id = await h.PendingAsync(h.Request("get", ["db"]));
        using (var device = h.Device())
            Assert.Equal(HttpStatusCode.NoContent, (await device.PostAsJsonAsync($"vault/approvals/{id}", new { decision = "approve" })).StatusCode);
        await h.DeviceWriteAsync("db", "db_password_B2");
        var answer = await h.PollAsync(id);
        Assert.Equal("denied", answer["status"]!.GetValue<string>());
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("db_password_B2")), answer.ToJsonString());
    }

    [Fact]
    public async Task Revoking_a_lease_after_a_device_replaced_the_value_scrubs_the_old_value_not_the_new()
    {
        using var h = await RecordVaultHarness.CreateAsync();
        await h.DeviceWriteAsync("db", "db_password_A1");
        await h.ApprovedAsync(h.Request("request", ["db"], new { ttl = 3600 }));
        await h.DeviceWriteAsync("db", "db_password_B2");
        var leaseId = (await h.LeasesAsync()).Single()!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, (await h.Bob.DeleteAsync($"/api/v1/vault/leases/{leaseId}")).StatusCode);
        h.App.Clock.Advance(TimeSpan.FromSeconds(10));
        var vm = (await h.App.Vms.GetAsync("dev", default))!;
        Assert.True(await h.Vault.ScrubDueAsync(vm, default));
        var jobs = (await h.Guest.GetFromJsonAsync<JsonObject>("/api/v1/vms/dev/vault/scrubs"))!.ToJsonString();
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("db_password_A1")), jobs);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("db_password_B2")), jobs);
    }

    [Fact]
    public async Task A_secret_deleted_on_a_device_ends_its_leases_at_the_VMs_next_request()
    {
        using var h = await RecordVaultHarness.CreateAsync();
        await h.DeviceWriteAsync("db", "db_password_1");
        await h.ApprovedAsync(h.Request("request", ["db"], new { ttl = 3600 }));
        await h.DeviceWriteAsync("db", "", delete: true);
        var status = await h.AnswerAsync(h.Request("status", []));
        Assert.Empty(status["items"]!.AsArray());
        Assert.Empty(await h.LeasesAsync());
        Assert.Contains("Access to db ended: it was deleted; scrubbing the VM.", await h.ActivityAsync());
    }

    // ---- secret entry on request (10.3) ----

    [Fact]
    public async Task Secret_ask_end_to_end_entered_on_a_device_needs_approval_and_grants_no_lease()
    {
        using var h = await RecordVaultHarness.CreateAsync();
        var asked = await h.AnswerAsync(h.Request("ask", ["jira"], new { description = "Jira API token", reason = "release notes" }));
        Assert.Equal("ok", asked["status"]!.GetValue<string>());
        var requestId = asked["requestId"]!.GetValue<string>();
        // Asking again for the same name is the same request.
        Assert.Equal(requestId, (await h.AnswerAsync(h.Request("ask", ["jira"], new { description = "again" })))["requestId"]!.GetValue<string>());

        var list = await h.AnswerAsync(h.Request("list", []));
        var item = list["items"]!.AsArray().Single()!;
        Assert.Equal("jira", item["name"]!.GetValue<string>());
        Assert.True(item["requested"]!.GetValue<bool>());

        using var device = h.Device();
        var requests = (await device.GetFromJsonAsync<JsonObject>("vault/entry-requests"))!["requests"]!.AsArray();
        var shown = requests.Single()!;
        Assert.Equal(requestId, shown["id"]!.GetValue<string>());
        Assert.Equal("dev", shown["vm"]!.GetValue<string>());
        Assert.Equal("jira", shown["name"]!.GetValue<string>());
        Assert.Equal("Jira API token", shown["description"]!.GetValue<string>());
        Assert.Equal("release notes", shown["reason"]!.GetValue<string>());
        Assert.Equal(h.Ms(VaultHostService_EntryLifetime), shown["expiresAt"]!.GetValue<long>());

        // Only a device enters a value: not the user's plain sign-in, not the VM.
        var stamp = h.Ms();
        var payload = VaultSeal.SealEntry(h.Keys.Vault, "jira", stamp, "bob@example", "jira_t0ken_value");
        using (var signedIn = await h.Bob.PostAsJsonAsync($"/api/v1/vault/entry-requests/{requestId}", new { payload, updatedAt = stamp, hasUsername = true }))
            Assert.Equal(HttpStatusCode.Forbidden, signedIn.StatusCode);
        using (var vm = await h.Guest.PostAsJsonAsync($"/api/v1/vault/entry-requests/{requestId}", new { payload, updatedAt = stamp, hasUsername = true }))
            Assert.Equal(HttpStatusCode.Forbidden, vm.StatusCode);
        // Never auto-approved by this path.
        var autoPayload = VaultSeal.SealEntry(h.Keys.Vault, "jira", stamp, "", "jira_t0ken_value", autoApprove: true);
        using (var auto = await device.PostAsJsonAsync($"vault/entry-requests/{requestId}", new { payload = autoPayload, updatedAt = stamp, hasUsername = false }))
            Assert.Equal(HttpStatusCode.BadRequest, auto.StatusCode);

        using (var saved = await device.PostAsJsonAsync($"vault/entry-requests/{requestId}", new { payload, updatedAt = stamp, hasUsername = true }))
            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        var record = (await h.ReadAsync("jira"))!;
        Assert.Equal(new SecretProvenance(SecretProvenance.EnteredOnRequest, "dev", h.EngineId, h.Laptop.Actor, requestId), record.Value.Provenance);
        Assert.False(record.Value.AutoApprove);
        Assert.Equal("Jira API token", record.Value.Description);
        Assert.Equal(("bob@example", "jira_t0ken_value", false), await h.OpenAsync("jira"));
        Assert.Empty((await device.GetFromJsonAsync<JsonObject>("vault/entry-requests"))!["requests"]!.AsArray());

        // No lease: the VM sees the entry and asks for it like any secret.
        Assert.Empty(await h.LeasesAsync());
        var after = (await h.AnswerAsync(h.Request("list", [])))["items"]!.AsArray().Single()!;
        Assert.Null(after["requested"]);
        Assert.Null(after["lease"]);
        Assert.Equal(HttpStatusCode.Accepted, (await h.AskAsync(h.Request("get", ["jira"]))).Status);
        // Asking for a name that exists says so.
        Assert.Equal("exists", (await h.AnswerAsync(h.Request("ask", ["jira"], new { description = "x" })))["status"]!.GetValue<string>());
    }

    private static readonly TimeSpan VaultHostService_EntryLifetime = Constructd.Core.Services.VaultHostService.EntryRequestLifetime;

    [Fact]
    public async Task Secret_ask_a_name_taken_meanwhile_is_not_overwritten_and_a_decline_removes_only_the_request()
    {
        using var h = await RecordVaultHarness.CreateAsync();
        var requestId = (await h.AnswerAsync(h.Request("ask", ["npm"], new { description = "npm token" })))["requestId"]!.GetValue<string>();
        var taken = await h.DeviceWriteAsync("npm", "npm_from_laptop");

        using var device = h.Device();
        var stamp = h.Ms(TimeSpan.FromSeconds(1));
        var payload = VaultSeal.SealEntry(h.Keys.Vault, "npm", stamp, "", "npm_entered_value");
        using (var saved = await device.PostAsJsonAsync($"vault/entry-requests/{requestId}", new { payload, updatedAt = stamp, hasUsername = false }))
        {
            Assert.Equal(HttpStatusCode.Conflict, saved.StatusCode);
            Assert.Equal("exists", (await saved.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        }

        Assert.Equal(taken.Revision, (await h.StoredAsync("npm"))!.Revision);
        Assert.Equal(("", "npm_from_laptop", false), await h.OpenAsync("npm"));

        using (var declined = await device.PostAsync($"vault/entry-requests/{requestId}/decline", null))
            Assert.Equal(HttpStatusCode.NoContent, declined.StatusCode);
        Assert.Empty((await device.GetFromJsonAsync<JsonObject>("vault/entry-requests"))!["requests"]!.AsArray());
        using (var again = await device.PostAsync($"vault/entry-requests/{requestId}/decline", null))
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(taken.Revision, (await h.StoredAsync("npm"))!.Revision);
    }

    [Fact]
    public async Task Secret_ask_expires_after_a_day()
    {
        using var h = await RecordVaultHarness.CreateAsync();
        var requestId = (await h.AnswerAsync(h.Request("ask", ["later"], new { description = "a token" })))["requestId"]!.GetValue<string>();
        h.App.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        await h.Vault.TickAsync(default);
        using var device = h.Device();
        Assert.Empty((await device.GetFromJsonAsync<JsonObject>("vault/entry-requests"))!["requests"]!.AsArray());
        Assert.Empty((await h.AnswerAsync(h.Request("list", [])))["items"]!.AsArray());
        var stamp = h.Ms();
        var payload = VaultSeal.SealEntry(h.Keys.Vault, "later", stamp, "", "too_late_value");
        using (var saved = await device.PostAsJsonAsync($"vault/entry-requests/{requestId}", new { payload, updatedAt = stamp, hasUsername = false }))
            Assert.Equal(HttpStatusCode.NotFound, saved.StatusCode);
        Assert.Null(await h.StoredAsync("later"));
    }

    // ---- vault modes (8.2) ----

    [Fact]
    public async Task Locked_mode_holds_no_K_until_a_device_unlocks_the_VM_or_makes_the_vault_available()
    {
        using var h = await RecordVaultHarness.CreateAsync(mode: "locked");
        var mode = (await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/mode"))!;
        Assert.Equal("locked", mode["mode"]!.GetValue<string>());
        Assert.False(mode["holdsKey"]!.GetValue<bool>());
        Assert.True(mode["records"]!.GetValue<bool>());

        // Names and approvals work without K; values do not.
        await h.DeviceWriteAsync("db", "db_password_1");
        Assert.Equal("db", (await h.AnswerAsync(h.Request("list", [])))["items"]!.AsArray().Single()!["name"]!.GetValue<string>());
        Assert.Equal("locked", (await h.AnswerAsync(h.Add("new-one", "value_123")))["status"]!.GetValue<string>());
        var (_, approved) = await h.ApprovedAsync(h.Request("request", ["db"], new { ttl = 3600 }));
        Assert.Equal("ok", approved["status"]!.GetValue<string>());
        Assert.Equal("locked", (await h.AnswerAsync(h.Request("get", ["db"])))["status"]!.GetValue<string>());

        // A grant that is not this vault's K is refused; the device's K unlocks the running VM.
        using var device = h.Device();
        using (var wrong = await device.PostAsJsonAsync("vms/dev/vault/unlock", new { grant = h.KeyGrant(new byte[32]) }))
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        using (var unlock = await device.PostAsJsonAsync("vms/dev/vault/unlock", new { grant = h.KeyGrant() }))
            Assert.Equal(HttpStatusCode.NoContent, unlock.StatusCode);
        var got = await h.AnswerAsync(h.Request("get", ["db"]));
        Assert.Equal("db_password_1", Encoding.UTF8.GetString(Convert.FromBase64String(got["secret"]!.GetValue<string>())));
        mode = (await device.GetFromJsonAsync<JsonObject>("vault/mode"))!;
        Assert.Equal("locked", mode["mode"]!.GetValue<string>());
        Assert.True(mode["holdsKey"]!.GetValue<bool>());
        Assert.Equal(["dev"], mode["unlockedVms"]!.AsArray().Select(v => v!.GetValue<string>()));

        // The VM stops: its key is gone.
        h.Vault.Drop("dev");
        Assert.False((await device.GetFromJsonAsync<JsonObject>("vault/mode"))!["holdsKey"]!.GetValue<bool>());

        // available: the device grants K; locked: it is withdrawn.
        using (var available = await device.PutAsJsonAsync("vault/mode", new { mode = "available", grant = h.KeyGrant() }))
            Assert.Equal(HttpStatusCode.NoContent, available.StatusCode);
        mode = (await device.GetFromJsonAsync<JsonObject>("vault/mode"))!;
        Assert.Equal("available", mode["mode"]!.GetValue<string>());
        Assert.True(mode["holdsKey"]!.GetValue<bool>());
        Assert.Equal("ok", (await h.AnswerAsync(h.Add("new-one", "value_123")))["status"]!.GetValue<string>());

        using (var locked = await device.PutAsJsonAsync("vault/mode", new { mode = "locked" }))
            Assert.Equal(HttpStatusCode.NoContent, locked.StatusCode);
        mode = (await device.GetFromJsonAsync<JsonObject>("vault/mode"))!;
        Assert.Equal("locked", mode["mode"]!.GetValue<string>());
        Assert.False(mode["holdsKey"]!.GetValue<bool>());
        Assert.Equal("locked", (await h.AnswerAsync(h.Request("get", ["db"])))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Available_mode_with_a_wrong_key_is_refused()
    {
        using var h = await RecordVaultHarness.CreateAsync(mode: "locked");
        await h.DeviceWriteAsync("db", "db_password_1");
        using var device = h.Device();
        using var response = await device.PutAsJsonAsync("vault/mode", new { mode = "available", grant = h.KeyGrant(new byte[32]) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("locked", (await device.GetFromJsonAsync<JsonObject>("vault/mode"))!["mode"]!.GetValue<string>());
    }

    // ---- the old-style vault, and users kept apart ----

    [Fact]
    public async Task An_old_style_user_on_the_same_engine_keeps_todays_vault()
    {
        using var h = await RecordVaultHarness.CreateAsync();
        using var carol = await h.App.CreateUserClientAsync("carol");
        var token = (await carol.CreateVmAsync("cvm")).VmToken();
        using var carolGuest = h.App.CreateVmTokenClient(token);
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        Assert.Equal(HttpStatusCode.NoContent, (await carol.PutAsJsonAsync("/api/v1/vault/settings",
            new { mode = "available", key = Convert.ToBase64String(key), keyCheck = VaultCrypto.KeyCheck(key) })).StatusCode);
        var stamp = h.Ms();
        using (var put = await carol.PutAsJsonAsync("/api/v1/vault/entries", new { entries = new[] { new { name = "c-token", description = "Carol's", hasUsername = false,
                   payload = VaultCrypto.SealEntry(key, "c-token", stamp, "", "carol_value_1"), updatedAt = stamp, updatedBy = "pc:c", deleted = false } } }))
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.False((await carol.GetFromJsonAsync<JsonObject>("/api/v1/vault/mode"))!["records"]!.GetValue<bool>());

        // Today's request path: an add goes to the table, ask is not offered.
        var (_, added) = await h.AskAsync(h.Add("c-new", "carol_value_2"), carolGuest, "cvm");
        Assert.Equal("ok", added["status"]!.GetValue<string>());
        var entries = (await carol.GetFromJsonAsync<JsonObject>("/api/v1/vault/entries"))!["entries"]!.AsArray();
        Assert.Equal(["c-new", "c-token"], entries.Select(e => e!["name"]!.GetValue<string>()).Order());
        var (_, ask) = await h.AskAsync(h.Request("ask", ["x"], new { description = "x" }), carolGuest, "cvm");
        Assert.Equal("error", ask["status"]!.GetValue<string>());
        Assert.Null(await h.StoredAsync("c-new"));
    }

    [Fact]
    public async Task A_VM_token_reaches_neither_another_users_secrets_nor_the_device_routes()
    {
        using var h = await RecordVaultHarness.CreateAsync();
        await h.DeviceWriteAsync("bob-only", "bob_secret_value");
        using var carol = await h.App.CreateUserClientAsync("carol");
        using var carolGuest = h.App.CreateVmTokenClient((await carol.CreateVmAsync("cvm")).VmToken());

        // bob's VM through carol's token: refused; carol's own list does not name bob's secrets.
        Assert.Equal(HttpStatusCode.Forbidden, (await h.AskAsync(h.Request("get", ["bob-only"]), carolGuest, "dev")).Status);
        var (_, list) = await h.AskAsync(h.Request("list", []), carolGuest, "cvm");
        Assert.DoesNotContain("bob-only", list.ToJsonString());
        var (_, get) = await h.AskAsync(h.Request("get", ["bob-only"]), carolGuest, "cvm");
        Assert.NotEqual("ok", get["status"]!.GetValue<string>());
        Assert.DoesNotContain("bob_secret_value", get.ToJsonString());

        // A VM token gets none of the user's or device's vault routes.
        foreach (var path in new[] { "/api/v1/vault/mode", "/api/v1/vault/entry-requests", "/api/v1/vault/entries", "/api/v1/vault/leases", "/api/v1/vault/approvals" })
            Assert.Equal(HttpStatusCode.Forbidden, (await h.Guest.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Guest.PutAsJsonAsync("/api/v1/vault/mode", new { mode = "locked" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Guest.PostAsJsonAsync("/api/v1/vms/dev/vault/unlock", new { grant = h.KeyGrant() })).StatusCode);
        // The store is not a VM's either.
        Assert.Contains((await h.Guest.GetAsync("/api/v1/store/records/secret%3Abob-only")).StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Guest.GetAsync("/api/v1/sync/records/secret%3Abob-only")).StatusCode);
    }

    // ---- C.2: no value in the database in clear, in a log or an audit row ----

    [Fact]
    public async Task No_value_reaches_the_database_the_log_or_the_audit_in_clear()
    {
        var root = Path.Combine(Path.GetTempPath(), "construct-vault-records-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        string[] values = ["vm_added_Zq81xPw4", "laptop_set_Kd02mVb7", "entered_Rr55tYu9", "replaced_Hh31nMx2"];
        try
        {
            var app = TestApp.WithSqlite(Path.Combine(root, "constructd.db"), configureServices: RacingRecords.Register);
            using (var h = await RecordVaultHarness.CreateAsync(app: app))
            {
                await h.AnswerAsync(h.Add("vm-added", values[0]));
                await h.DeviceWriteAsync("laptop-set", values[1]);
                var requestId = (await h.AnswerAsync(h.Request("ask", ["entered"], new { description = "entered" })))["requestId"]!.GetValue<string>();
                var stamp = h.Ms();
                using (var device = h.Device())
                    Assert.Equal(HttpStatusCode.NoContent, (await device.PostAsJsonAsync($"vault/entry-requests/{requestId}",
                        new { payload = VaultSeal.SealEntry(h.Keys.Vault, "entered", stamp, "", values[2]), updatedAt = stamp, hasUsername = false })).StatusCode);
                await h.ApprovedAsync(h.Add("laptop-set", values[3]).Also(r => r["replace"] = true));
                // The VM that added it holds it for an hour.
                var got = await h.AnswerAsync(h.Request("get", ["vm-added"]));
                Assert.Equal(values[0], Encoding.UTF8.GetString(Convert.FromBase64String(got["secret"]!.GetValue<string>())));
                await h.Vault.TickAsync(default);

                var logs = h.App.Logs.AllText();
                foreach (var value in values)
                {
                    Assert.DoesNotContain(value, logs);
                    Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)), logs);
                }
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var bytes = await File.ReadAllBytesAsync(file);
                foreach (var value in values)
                {
                    foreach (var form in new[] { value, Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) })
                        Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(form)) < 0, $"{Path.GetFileName(file)} holds {form}");
                }
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}

internal static class JsonObjectExtensions
{
    public static JsonObject Also(this JsonObject node, Action<JsonObject> change)
    {
        change(node);
        return node;
    }
}
