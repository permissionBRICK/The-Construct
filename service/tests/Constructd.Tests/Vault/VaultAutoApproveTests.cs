using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Tests.Vault;

/// <summary>Auto-approved secrets: read without the user's approval, never scrubbed, and switched on only with the user's yes.</summary>
public sealed class VaultAutoApproveTests
{
    private static string[] Strings(JsonNode? array) => array!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
    private static async Task<JsonArray> LeasesAsync(VaultHarness h) => (await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/leases"))!["leases"]!.AsArray();
    private static Task<IReadOnlyList<VaultCleanup>> CleanupsAsync(VaultHarness h) => h.App.Service<IVaultStore>().ListAsync<VaultCleanup>("bob", default);
    private static async Task<JsonObject> EntryAsync(VaultHarness h, string name) =>
        (await h.EntriesAsync())["entries"]!.AsArray().Single(e => e!["name"]!.GetValue<string>() == name)!.AsObject();

    [Fact]
    public async Task An_auto_approved_secret_is_granted_and_read_without_asking_or_a_lease()
    {
        using var h = await VaultHarness.CreateAsync();
        await h.PutEntriesAsync(h.Entry("pushover", "po-token-123", description: "Pushover", autoApprove: true), h.Entry("db", "pw-123456"));
        Assert.True((await EntryAsync(h, "pushover"))["autoApprove"]!.GetValue<bool>());
        Assert.False((await EntryAsync(h, "db"))["autoApprove"]!.GetValue<bool>());

        var items = (await h.AnswerAsync(h.Request("list", [])))["items"]!.AsArray();
        Assert.Equal([false, true], items.Select(i => i!["autoApprove"]!.GetValue<bool>()));
        // status lists what this VM may read now: an auto-approved secret always.
        Assert.Equal(["pushover"], (await h.AnswerAsync(h.Request("status", [])))["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()));

        var granted = await h.AnswerAsync(h.Request("request", ["pushover"], new { ttl = 600 }));
        Assert.Equal("ok", granted["status"]!.GetValue<string>());
        Assert.Equal("Approved.", granted["message"]!.GetValue<string>());
        Assert.Equal(["pushover"], Strings(granted["names"]));
        Assert.Equal(["pushover"], Strings(granted["auto"]));
        Assert.Null(granted["lease"]);
        Assert.Empty(await LeasesAsync(h));

        var events = (await h.ActivityAsync()).Length;
        var read = await h.AnswerAsync(h.Request("get", ["pushover"]));
        Assert.Equal("po-token-123", VaultHarness.Secret(read));
        Assert.Null(read["lease"]);
        Assert.Empty(await LeasesAsync(h));
        Assert.Empty(await CleanupsAsync(h));
        Assert.Equal(events, (await h.ActivityAsync()).Length);
        Assert.Contains("Access to pushover auto-approved.", await h.ActivityAsync());
    }

    [Fact]
    public async Task A_mixed_request_asks_only_about_the_others()
    {
        using var h = await VaultHarness.CreateAsync();
        await h.PutEntriesAsync(h.Entry("pushover", "po-token-123", autoApprove: true), h.Entry("db", "pw-123456"));

        var id = await h.PendingAsync(h.Request("request", ["pushover", "db"], new { uses = 2 }));
        var approval = (await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/approvals"))!["approvals"]!.AsArray().Single()!;
        Assert.Equal(["db"], Strings(approval["names"]));
        Assert.DoesNotContain("pushover", approval["message"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, await h.DecideAsync(id, "approve"));
        var approved = (await h.PollAsync(id)).Body!;
        Assert.Equal(["pushover", "db"], Strings(approved["names"]));
        Assert.Equal(["pushover"], Strings(approved["auto"]));
        Assert.Equal(2, approved["lease"]!["usesLeft"]!.GetValue<int>());
        Assert.Equal("db", (await LeasesAsync(h)).Single()!["name"]!.GetValue<string>());
        Assert.Contains("Access to pushover auto-approved.", await h.ActivityAsync());

        var denied = await h.PendingAsync(h.Request("request", ["pushover", "db"]));
        Assert.Equal(HttpStatusCode.NoContent, await h.DecideAsync(denied, "deny"));
        Assert.Equal("The user denied the request. pushover needs no approval: read it with construct secret get.",
            (await h.PollAsync(denied)).Body!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Leases_on_a_secret_that_became_auto_approved_end_without_a_scrub()
    {
        using var h = await VaultHarness.CreateAsync();
        await h.PutEntriesAsync(h.Entry("a-token", "value-aaaaaa", at: 1000), h.Entry("b-token", "value-bbbbbb", at: 1000),
            h.Entry("c-token", "value-cccccc", at: 1000), h.Entry("d-token", "value-dddddd", at: 1000));
        await h.ApprovedAsync(h.Request("request", ["a-token", "b-token", "c-token", "d-token"], new { ttl = 600 }));
        // The PC switches three of them to auto-approve: a flag change keeps the leases.
        await h.PutEntriesAsync(h.Entry("b-token", "value-bbbbbb", at: 2000, autoApprove: true), h.Entry("c-token", "value-cccccc", at: 2000, autoApprove: true),
            h.Entry("d-token", "value-dddddd", at: 2000, autoApprove: true));
        Assert.Equal(4, (await LeasesAsync(h)).Count);

        var released = await h.AnswerAsync(h.Request("release", ["a-token", "d-token"]));
        Assert.Equal(["a-token", "d-token"], Strings(released["names"]).Order());
        Assert.Equal(["d-token"], Strings(released["auto"]));
        var c = (await LeasesAsync(h)).Single(l => l!["name"]!.GetValue<string>() == "c-token")!["id"]!.GetValue<string>();
        using (var revoke = await h.Bob.DeleteAsync($"/api/v1/vault/leases/{c}"))
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        h.App.Clock.Advance(TimeSpan.FromMinutes(11));
        await h.Vault.TickAsync(default);

        var activity = await h.ActivityAsync();
        Assert.Contains("Released a-token; scrubbing the VM.", activity);
        Assert.Contains("Released d-token (auto-approved, no scrub).", activity);
        Assert.Contains("Revoked access to c-token.", activity);
        Assert.Contains("Access to b-token expired.", activity);
        Assert.Empty(await LeasesAsync(h));
        // Only a-token is scrubbed.
        var job = (await h.App.Service<IVaultStore>().ListAsync<VaultScrubJob>("bob", default)).Single();
        Assert.Equal(["a-token"], job.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task A_due_scrub_of_a_secret_that_became_auto_approved_is_dropped()
    {
        using var h = await VaultHarness.CreateAsync();
        await h.PutEntriesAsync(h.Entry("db", "pw-123456-long", at: 1000));
        await h.ApprovedAsync(h.Request("request", ["db"]));
        await h.AnswerAsync(h.Request("release", ["db"]));
        Assert.Single(await CleanupsAsync(h));

        await h.PutEntriesAsync(h.Entry("db", "pw-123456-long", at: 2000, autoApprove: true));
        h.App.Clock.Advance(TimeSpan.FromSeconds(6));
        await h.Vault.TickAsync(default);
        Assert.Empty(await CleanupsAsync(h));
        Assert.Empty(await h.App.Service<IVaultStore>().ListAsync<VaultScrubJob>("bob", default));
    }

    [Fact]
    public async Task A_new_value_scrubs_by_the_setting_of_the_value_the_vm_saw()
    {
        using var h = await VaultHarness.CreateAsync();
        await h.PutEntriesAsync(h.Entry("auto", "value-aaaaaa", at: 1000), h.Entry("plain", "value-pppppp", at: 1000));
        await h.ApprovedAsync(h.Request("request", ["auto", "plain"]));
        await h.PutEntriesAsync(h.Entry("auto", "value-aaaaaa", at: 2000, autoApprove: true));

        // auto's old value was auto-approved: no scrub. plain's old value was not: scrubbed, although the new one is.
        await h.PutEntriesAsync(h.Entry("auto", "value-a2a2a2", at: 3000, autoApprove: true), h.Entry("plain", "value-p2p2p2", at: 3000, autoApprove: true));
        Assert.Empty(await LeasesAsync(h));
        var cleanup = (await CleanupsAsync(h)).Single();
        Assert.Equal("plain", cleanup.Copy.Name);
        Assert.Equal(("", "value-pppppp"), VaultCrypto.OpenEntry(h.Key, "plain", cleanup.Copy.UpdatedAt, cleanup.Copy.Payload));
    }

    [Fact]
    public async Task Add_can_store_an_auto_approved_secret_and_a_replace_keeps_or_sets_the_setting()
    {
        using var h = await VaultHarness.CreateAsync();
        var add = await h.AnswerAsync(h.Request("add", ["po"], new { secret = Convert.ToBase64String("po-token-1"u8.ToArray()), description = "Pushover", autoApprove = true }));
        Assert.Equal("Stored.", add["message"]!.GetValue<string>());
        Assert.True(add["autoApprove"]!.GetValue<bool>());
        Assert.Null(add["lease"]);
        Assert.Empty(await LeasesAsync(h));
        var entry = await EntryAsync(h, "po");
        Assert.True(entry["autoApprove"]!.GetValue<bool>());
        Assert.True(h.Sealed(entry)["autoApprove"]!.GetValue<bool>());
        Assert.Contains("Stored po from the VM; auto-approved.", await h.ActivityAsync());

        // A replace without a setting keeps it.
        var kept = await h.ApprovedAsync(h.Request("add", ["po"], new { secret = Convert.ToBase64String("po-token-2"u8.ToArray()), description = "Pushover", replace = true }));
        Assert.True(kept["autoApprove"]!.GetValue<bool>());
        Assert.Null(kept["lease"]);
        Assert.True((await EntryAsync(h, "po"))["autoApprove"]!.GetValue<bool>());

        // A plain add leases the secret to the VM; a replace that turns auto-approve on says so and scrubs the old value.
        var plain = await h.AnswerAsync(h.Request("add", ["db"], new { secret = Convert.ToBase64String("pw-123456"u8.ToArray()), description = "DB" }));
        Assert.False(plain["autoApprove"]!.GetValue<bool>());
        Assert.NotNull(plain["lease"]);
        var id = await h.PendingAsync(h.Request("add", ["db"], new { secret = Convert.ToBase64String("pw-999999"u8.ToArray()), description = "DB", replace = true, autoApprove = true }));
        var approval = (await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/approvals"))!["approvals"]!.AsArray().Single()!;
        Assert.Contains("It also becomes auto-approved: agents on all your VMs read it without asking you, and no VM is scrubbed of it.", approval["message"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, await h.DecideAsync(id, "approve"));
        Assert.True((await h.PollAsync(id)).Body!["autoApprove"]!.GetValue<bool>());
        Assert.Empty(await LeasesAsync(h));
        Assert.Equal("db", (await CleanupsAsync(h)).Single().Copy.Name);
        Assert.True((await EntryAsync(h, "db"))["autoApprove"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_vm_turns_auto_approve_off_at_once_and_on_only_with_the_users_yes()
    {
        using var h = await VaultHarness.CreateAsync();
        await h.PutEntriesAsync(h.Entry("db", "pw-123456", "admin", "Staging DB", at: 1000));
        Assert.Equal("notFound", (await h.AnswerAsync(h.Request("autoApprove", ["nope"], new { autoApprove = true })))["status"]!.GetValue<string>());
        var already = await h.AnswerAsync(h.Request("autoApprove", ["db"], new { autoApprove = false }));
        Assert.Equal("Already needs approval.", already["message"]!.GetValue<string>());
        Assert.False(already["autoApprove"]!.GetValue<bool>());

        var refused = await h.PendingAsync(h.Request("autoApprove", ["db"], new { autoApprove = true }));
        Assert.Equal(HttpStatusCode.NoContent, await h.DecideAsync(refused, "deny"));
        Assert.Equal("The user denied the request.", (await h.PollAsync(refused)).Body!["message"]!.GetValue<string>());
        Assert.False((await EntryAsync(h, "db"))["autoApprove"]!.GetValue<bool>());

        var id = await h.PendingAsync(h.Request("autoApprove", ["db"], new { autoApprove = true, reason = "cron job" }));
        var approval = (await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/approvals"))!["approvals"]!.AsArray().Single()!;
        Assert.Equal("autoApprove", approval["op"]!.GetValue<string>());
        Assert.Equal("Key vault — auto-approve secret", approval["title"]!.GetValue<string>());
        Assert.Equal("Auto-approve", approval["action"]!.GetValue<string>());
        Assert.Equal(["db"], Strings(approval["names"]));
        Assert.Equal(HttpStatusCode.NoContent, await h.DecideAsync(id, "approve"));
        var on = (await h.PollAsync(id)).Body!;
        Assert.Equal("Auto-approved.", on["message"]!.GetValue<string>());
        Assert.True(on["autoApprove"]!.GetValue<bool>());
        var entry = await EntryAsync(h, "db");
        Assert.True(entry["autoApprove"]!.GetValue<bool>());
        Assert.Equal("vm:dev", entry["updatedBy"]!.GetValue<string>());
        Assert.True(entry["updatedAt"]!.GetValue<long>() > 1000);
        var sealedOn = h.Sealed(entry);
        Assert.True(sealedOn["autoApprove"]!.GetValue<bool>());
        Assert.Equal(("admin", "pw-123456"), (sealedOn["username"]!.GetValue<string>(), sealedOn["secret"]!.GetValue<string>()));
        Assert.Equal("Already auto-approved.", (await h.AnswerAsync(h.Request("autoApprove", ["db"], new { autoApprove = true })))["message"]!.GetValue<string>());

        var off = await h.AnswerAsync(h.Request("autoApprove", ["db"], new { autoApprove = false }));
        Assert.Equal("Needs approval again.", off["message"]!.GetValue<string>());
        entry = await EntryAsync(h, "db");
        Assert.False(entry["autoApprove"]!.GetValue<bool>());
        Assert.False(h.Sealed(entry)["autoApprove"]!.GetValue<bool>());
        var activity = await h.ActivityAsync();
        Assert.Contains("Auto-approve for db approved at the VM's request.", activity);
        Assert.Contains("db needs your approval again (the VM turned auto-approve off).", activity);
        Assert.Equal(HttpStatusCode.Accepted, (await h.AskAsync(h.Request("get", ["db"]))).Status);
    }

    [Fact]
    public async Task Changing_the_setting_needs_the_vault_key()
    {
        using var h = await VaultHarness.CreateAsync("locked");
        await h.PutEntriesAsync(h.Entry("db", "pw-123456"), h.Entry("po", "po-token-123", autoApprove: true));
        Assert.Equal("locked", (await h.AnswerAsync(h.Request("autoApprove", ["db"], new { autoApprove = true })))["status"]!.GetValue<string>());
        Assert.Equal("locked", (await h.AnswerAsync(h.Request("autoApprove", ["po"], new { autoApprove = false })))["status"]!.GetValue<string>());
        // Reading needs it too; a request does not.
        Assert.Equal("locked", (await h.AnswerAsync(h.Request("get", ["po"])))["status"]!.GetValue<string>());
        Assert.Equal("ok", (await h.AnswerAsync(h.Request("request", ["po"])))["status"]!.GetValue<string>());
        var invalid = await h.AnswerAsync(h.Request("autoApprove", ["db"]));
        Assert.Equal("invalid", invalid["status"]!.GetValue<string>());
    }
}
