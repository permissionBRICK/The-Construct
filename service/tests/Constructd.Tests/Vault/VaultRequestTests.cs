using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Tests.Vault;

/// <summary>The guest request operations end to end: approvals, long polls, leases, add and delete.</summary>
public sealed class VaultRequestTests
{
    [Fact]
    public async Task List_and_status_show_names_and_descriptions_never_values()
    {
        using var h = await VaultHarness.CreateAsync();
        await h.PutEntriesAsync(h.Entry("github-token", "ghp_value_1", description: "GitHub"), h.Entry("db", "pw-123456", "admin", "Staging DB"));
        var list = await h.AnswerAsync(h.Request("list", []));
        Assert.Equal(1, list["v"]!.GetValue<int>());
        Assert.Equal("ok", list["status"]!.GetValue<string>());
        var items = list["items"]!.AsArray();
        Assert.Equal(["db", "github-token"], items.Select(i => i!["name"]!.GetValue<string>()));
        Assert.True(items[0]!["hasUsername"]!.GetValue<bool>());
        Assert.Null(items[0]!["lease"]);
        Assert.DoesNotContain("pw-123456", list.ToJsonString(), StringComparison.Ordinal);
        Assert.Empty((await h.AnswerAsync(h.Request("status", [])))["items"]!.AsArray());

        await h.ApprovedAsync(h.Request("request", ["db"], new { uses = 2 }));
        var status = (await h.AnswerAsync(h.Request("status", [])))["items"]!.AsArray();
        Assert.Equal("db", status.Single()!["name"]!.GetValue<string>());
        Assert.Equal(2, status.Single()!["lease"]!["usesLeft"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_request_waits_in_a_long_poll_until_the_user_approves()
    {
        using var h = await VaultHarness.CreateAsync();
        await h.PutEntriesAsync(h.Entry("db", "pw-123456", "admin"));
        var id = await h.PendingAsync(h.Request("request", ["db"], new { uses = 2, ttl = 7200, reason = "migrate" }));

        Assert.Equal(HttpStatusCode.NoContent, (await h.PollAsync(id)).Status);
        var poll = h.PollAsync(id, wait: 20);
        await Task.Delay(200);
        Assert.False(poll.IsCompleted);
        var approval = (await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/approvals"))!["approvals"]!.AsArray().Single()!;
        Assert.Contains("Access: 2 uses, for 2 h", approval["message"]!.GetValue<string>());
        Assert.Contains("Reason given: “migrate”", approval["message"]!.GetValue<string>());
        Assert.Equal(2, approval["uses"]!.GetValue<int>());
        Assert.Equal(7200, approval["ttlSeconds"]!.GetValue<int>());
        Assert.Equal(h.Ms(TimeSpan.FromMinutes(10)), approval["deadline"]!.GetValue<long>());

        Assert.Equal(HttpStatusCode.NoContent, await h.DecideAsync(id, "approve"));
        var (status, body) = await poll.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ok", body!["status"]!.GetValue<string>());
        Assert.Equal(["db"], body["names"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(2, body["lease"]!["usesLeft"]!.GetValue<int>());
        Assert.Equal(h.Ms(TimeSpan.FromHours(2)), body["lease"]!["expiresAt"]!.GetValue<long>());
        // The answer is delivered once.
        Assert.Equal(HttpStatusCode.NotFound, (await h.PollAsync(id)).Status);

        // Two uses, then the lease is gone and a get asks again.
        var first = await h.AnswerAsync(h.Request("get", ["db"]));
        Assert.Equal("pw-123456", VaultHarness.Secret(first));
        Assert.Equal("admin", first["username"]!.GetValue<string>());
        Assert.Equal(1, first["lease"]!["usesLeft"]!.GetValue<int>());
        var second = await h.AnswerAsync(h.Request("get", ["db"]));
        Assert.Equal(0, second["lease"]!["usesLeft"]!.GetValue<int>());
        Assert.Empty((await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/leases"))!["leases"]!.AsArray());
        Assert.Equal(HttpStatusCode.Accepted, (await h.AskAsync(h.Request("get", ["db"]))).Status);
    }

    [Fact]
    public async Task Deny_and_the_deadline_answer_denied_and_the_first_answer_wins()
    {
        using var h = await VaultHarness.CreateAsync();
        await h.PutEntriesAsync(h.Entry("db", "pw-123456"));

        var denied = await h.PendingAsync(h.Request("request", ["db"]));
        var poll = h.PollAsync(denied, wait: 20);
        Assert.Equal(HttpStatusCode.NoContent, await h.DecideAsync(denied, "deny"));
        var (_, body) = await poll.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("denied", body!["status"]!.GetValue<string>());
        Assert.Equal("The user denied the request.", body["message"]!.GetValue<string>());

        var raced = await h.PendingAsync(h.Request("request", ["db"]));
        Assert.Equal(HttpStatusCode.NoContent, await h.DecideAsync(raced, "approve"));
        using (var late = await h.Bob.PostAsJsonAsync($"/api/v1/vault/approvals/{raced}", new { decision = "deny" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
            Assert.Equal("already-decided", (await late.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        }
        Assert.Equal("ok", (await h.PollAsync(raced)).Body!["status"]!.GetValue<string>());

        var expired = await h.PendingAsync(h.Request("request", ["db"]));
        h.App.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Empty((await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/approvals"))!["approvals"]!.AsArray());
        Assert.Equal(HttpStatusCode.Conflict, await h.DecideAsync(expired, "approve"));
        var timedOut = (await h.PollAsync(expired)).Body!;
        Assert.Equal("denied", timedOut["status"]!.GetValue<string>());
        Assert.Equal("No answer from the user before the request timed out.", timedOut["message"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.NotFound, (await h.PollAsync("0123456789abcdef")).Status);
        Assert.Equal(HttpStatusCode.NotFound, await h.DecideAsync("0123456789abcdef", "approve"));
        using var bad = await h.Bob.PostAsJsonAsync($"/api/v1/vault/approvals/{raced}", new { decision = "maybe" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task A_get_without_a_lease_asks_once_and_reads_the_value_at_pickup()
    {
        using var h = await VaultHarness.CreateAsync();
        await h.PutEntriesAsync(h.Entry("db", "pw-123456", description: "Staging DB"));
        var id = await h.PendingAsync(h.Request("get", ["db"]));
        var approval = (await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/approvals"))!["approvals"]!.AsArray().Single()!;
        Assert.Equal("Key vault — one-time access", approval["title"]!.GetValue<string>());
        Assert.Equal("Allow once", approval["action"]!.GetValue<string>());
        Assert.Equal(1, approval["uses"]!.GetValue<int>());
        Assert.Equal(600, approval["ttlSeconds"]!.GetValue<int>());

        Assert.Equal(HttpStatusCode.NoContent, await h.DecideAsync(id, "approve"));
        var answer = (await h.PollAsync(id)).Body!;
        Assert.Equal("pw-123456", VaultHarness.Secret(answer));
        Assert.Equal(0, answer["lease"]!["usesLeft"]!.GetValue<int>());

        // The used-up lease scrubs the VM a minute later.
        var cleanups = await h.App.Service<IVaultStore>().ListAsync<VaultCleanup>("bob", default);
        Assert.Equal(h.App.Clock.UtcNow + TimeSpan.FromMinutes(1), cleanups.Single().DueAt);
        var missing = await h.AnswerAsync(h.Request("get", ["nope"]));
        Assert.Equal("notFound", missing["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Leases_expire_and_can_be_revoked_or_released_into_scrubs()
    {
        using var h = await VaultHarness.CreateAsync();
        await h.PutEntriesAsync(h.Entry("a-token", "value-aaaaaa"), h.Entry("b-token", "value-bbbbbb"), h.Entry("c-token", "value-cccccc"));
        await h.ApprovedAsync(h.Request("request", ["a-token", "b-token", "c-token"], new { ttl = 600 }));
        var leases = (await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/leases"))!["leases"]!.AsArray();
        Assert.Equal(3, leases.Count);
        Assert.All(leases, l => Assert.Equal("approved", l!["origin"]!.GetValue<string>()));

        var b = leases.Single(l => l!["name"]!.GetValue<string>() == "b-token")!["id"]!.GetValue<string>();
        using (var revoke = await h.Bob.DeleteAsync($"/api/v1/vault/leases/{b}"))
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        var released = await h.AnswerAsync(h.Request("release", ["c-token"]));
        Assert.Equal(["c-token"], released["names"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("Nothing to release.", (await h.AnswerAsync(h.Request("release", ["c-token"])))["message"]!.GetValue<string>());

        h.App.Clock.Advance(TimeSpan.FromMinutes(11));
        await h.Vault.TickAsync(default);
        Assert.Empty((await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/leases"))!["leases"]!.AsArray());
        var activity = (await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/activity"))!["events"]!.AsArray().Select(e => e!["text"]!.GetValue<string>()).ToArray();
        Assert.Contains("Revoked access to b-token; scrubbing the VM.", activity);
        Assert.Contains("Released c-token; scrubbing the VM.", activity);
        Assert.Contains("Access to a-token expired; scrubbing the VM.", activity);
        // All three wait in one scan job for this VM.
        var job = (await h.App.Service<IVaultStore>().ListAsync<VaultScrubJob>("bob", default)).Single();
        Assert.Equal(["a-token", "b-token", "c-token"], job.Items.Select(i => i.Name).Order());
    }

    [Fact]
    public async Task Add_stores_an_encrypted_entry_without_approval_and_replace_needs_one()
    {
        using var h = await VaultHarness.CreateAsync();
        var add = await h.AnswerAsync(h.Request("add", ["new-token"], new
        {
            secret = Convert.ToBase64String("agent-secret-1"u8.ToArray()), description = "Made by the agent", username = "svc", ttl = 1800,
        }));
        Assert.Equal("ok", add["status"]!.GetValue<string>());
        Assert.Equal(h.Ms(TimeSpan.FromMinutes(30)), add["lease"]!["expiresAt"]!.GetValue<long>());

        var entry = (await h.EntriesAsync())["entries"]!.AsArray().Single()!;
        Assert.Equal("Made by the agent", entry["description"]!.GetValue<string>());
        Assert.True(entry["hasUsername"]!.GetValue<bool>());
        Assert.Equal("vm:dev", entry["updatedBy"]!.GetValue<string>());
        Assert.Equal(("svc", "agent-secret-1"), VaultCrypto.OpenEntry(h.Key, "new-token", entry["updatedAt"]!.GetValue<long>(), entry["payload"]!.GetValue<string>()));
        Assert.Equal("added", (await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/leases"))!["leases"]!.AsArray().Single()!["origin"]!.GetValue<string>());

        var exists = await h.AnswerAsync(h.Request("add", ["new-token"], new { secret = "eA==", description = "again" }));
        Assert.Equal("exists", exists["status"]!.GetValue<string>());

        var replace = await h.ApprovedAsync(h.Request("add", ["new-token"], new { secret = Convert.ToBase64String("agent-secret-2"u8.ToArray()), description = "Rotated", replace = true }));
        Assert.Equal("ok", replace["status"]!.GetValue<string>());
        var rotated = (await h.EntriesAsync())["entries"]!.AsArray().Single()!;
        Assert.True(rotated["updatedAt"]!.GetValue<long>() > entry["updatedAt"]!.GetValue<long>());
        Assert.Equal(("", "agent-secret-2"), VaultCrypto.OpenEntry(h.Key, "new-token", rotated["updatedAt"]!.GetValue<long>(), rotated["payload"]!.GetValue<string>()));
        // The VM saw the old value: it is scrubbed of it.
        var cleanup = (await h.App.Service<IVaultStore>().ListAsync<VaultCleanup>("bob", default)).Single();
        Assert.Equal(entry["payload"]!.GetValue<string>(), cleanup.Copy.Payload);
    }

    [Fact]
    public async Task Add_needs_the_key_and_delete_writes_a_tombstone_after_approval()
    {
        using var h = await VaultHarness.CreateAsync("locked");
        await h.PutEntriesAsync(h.Entry("db", "pw-123456"));
        var locked = await h.AnswerAsync(h.Request("add", ["x-token"], new { secret = "eHl6eHl6", description = "x" }));
        Assert.Equal("locked", locked["status"]!.GetValue<string>());

        // Delete needs no key, only the user's yes.
        var deleted = await h.ApprovedAsync(h.Request("delete", ["db"], new { reason = "rotated" }));
        Assert.Equal("Deleted.", deleted["message"]!.GetValue<string>());
        var tombstone = (await h.EntriesAsync())["entries"]!.AsArray().Single()!;
        Assert.True(tombstone["deleted"]!.GetValue<bool>());
        Assert.Equal("vm:dev", tombstone["updatedBy"]!.GetValue<string>());
        Assert.Equal("notFound", (await h.AnswerAsync(h.Request("get", ["db"])))["status"]!.GetValue<string>());
        Assert.Empty((await h.AnswerAsync(h.Request("list", [])))["items"]!.AsArray());
    }

    [Fact]
    public async Task A_new_value_from_the_pc_ends_leases_on_the_old_one()
    {
        using var h = await VaultHarness.CreateAsync();
        await h.PutEntriesAsync(h.Entry("db", "pw-123456", description: "one", at: 1000));
        await h.ApprovedAsync(h.Request("request", ["db"]));

        // Same value, new description: access stays.
        await h.PutEntriesAsync(h.Entry("db", "pw-123456", description: "two", at: 2000));
        Assert.Single((await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/leases"))!["leases"]!.AsArray());

        // A new value: the lease ends and the VM is scrubbed of the value it saw.
        await h.PutEntriesAsync(h.Entry("db", "pw-999999", at: 3000));
        Assert.Empty((await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/leases"))!["leases"]!.AsArray());
        var cleanup = (await h.App.Service<IVaultStore>().ListAsync<VaultCleanup>("bob", default)).Single();
        Assert.Equal(("", "pw-123456"), VaultCrypto.OpenEntry(h.Key, "db", cleanup.Copy.UpdatedAt, cleanup.Copy.Payload));
    }

    [Fact]
    public async Task Invalid_requests_answer_invalid_and_bodies_are_bounded()
    {
        using var h = await VaultHarness.CreateAsync();
        var invalid = await h.AnswerAsync(new { op = "request", names = new[] { "a", "b" }, uses = 5000 });
        Assert.Equal("invalid", invalid["status"]!.GetValue<string>());
        Assert.Equal("--uses must be between 1 and 1000.", invalid["message"]!.GetValue<string>());

        using (var notJson = await h.Guest.PostAsync("/api/v1/vms/dev/vault/requests", new StringContent("[1,2]", System.Text.Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
        using (var huge = await h.Guest.PostAsync("/api/v1/vms/dev/vault/requests",
                   new StringContent("{\"op\":\"list\",\"pad\":\"" + new string('x', 70000) + "\"}", System.Text.Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
        using (var wait = await h.Guest.GetAsync("/api/v1/vms/dev/vault/requests/0123456789abcdef?wait=abc"))
            Assert.Equal(HttpStatusCode.BadRequest, wait.StatusCode);
    }
}
