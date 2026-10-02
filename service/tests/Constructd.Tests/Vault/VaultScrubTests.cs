using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Constructd.Core.Logic;

namespace Constructd.Tests.Vault;

/// <summary>Scrubs on hosted VMs: heartbeat flag, scan, classification, file decisions and apply results.</summary>
public sealed class VaultScrubTests
{
    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    private static string Text(JsonNode? node) => Encoding.UTF8.GetString(Convert.FromBase64String(node!.GetValue<string>()));

    private static async Task<JsonArray> JobsAsync(VaultHarness h) =>
        (await h.Guest.GetFromJsonAsync<JsonObject>("/api/v1/vms/dev/vault/scrubs"))!["jobs"]!.AsArray();

    private static async Task PostAsync(VaultHarness h, string id, object body)
    {
        using var response = await h.Guest.PostAsJsonAsync($"/api/v1/vms/dev/vault/scrubs/{id}", body);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<string[]> ActivityAsync(VaultHarness h) =>
        (await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/activity"))!["events"]!.AsArray().Select(e => e!["text"]!.GetValue<string>()).ToArray();

    [Fact]
    public async Task A_finished_lease_is_scanned_agent_logs_are_redacted_and_other_files_wait_for_the_user()
    {
        using var h = await VaultHarness.CreateAsync();
        await h.PutEntriesAsync(h.Entry("github-token", "ghp_secret_value_1", "bot"), h.Entry("short", "abc"));
        await h.ApprovedAsync(h.Request("request", ["github-token", "short"], new { ttl = 600 }));
        Assert.Empty(await h.HeartbeatAsync());

        h.App.Clock.Advance(TimeSpan.FromMinutes(11));
        await h.Vault.TickAsync(default);
        Assert.True((await h.HeartbeatAsync())["vaultScrub"]!.GetValue<bool>());

        // The scan: patterns per secret index, computed now; a too-short value is reported, not searched.
        var scan = (await JobsAsync(h)).Single()!;
        Assert.Equal("scan", scan["step"]!.GetValue<string>());
        var patterns = scan["patterns"]!.AsArray().Select(p => (Index: p!["index"]!.GetValue<int>(), Text: Text(p["pattern"]))).ToArray();
        var tokenIndex = patterns.Single(p => p.Text == "ghp_secret_value_1").Index;
        Assert.Contains(patterns, p => p.Index == tokenIndex && p.Text == Convert.ToBase64String(Encoding.UTF8.GetBytes("bot:ghp_secret_value_1")));
        Assert.DoesNotContain(patterns, p => p.Text == "abc");
        // Delivered: not due again until the guest answers (or the redelivery timeout).
        Assert.Empty(await JobsAsync(h));
        Assert.Empty(await h.HeartbeatAsync());

        await PostAsync(h, scan["id"]!.GetValue<string>(), new
        {
            step = "scan", complete = true,
            hits = new object[]
            {
                new { path = B64("/root/.claude/projects/-root-repo/abc.jsonl"), indexes = new[] { tokenIndex }, size = 2048, type = "text" },
                new { path = B64("/root/.t3/userdata/state.sqlite-wal"), indexes = new[] { tokenIndex }, size = 4096, type = "sqlite-aux" },
                new { path = B64("/root/repo/.env"), indexes = new[] { tokenIndex }, size = 30, type = "text" },
                new { path = B64("relative/path"), indexes = new[] { tokenIndex }, size = 1, type = "text" },
                new { path = B64("/root/other.txt"), indexes = new[] { 99 }, size = 1, type = "text" },
            },
        });
        var activity = await ActivityAsync(h);
        Assert.Contains("short is too short to search for; not scrubbed.", activity);

        // The user sees the one other file; the agent logs are already an apply job.
        var files = (await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/files"))!["files"]!.AsArray();
        var file = files.Single()!;
        Assert.Equal("/root/repo/.env", file["path"]!.GetValue<string>());
        Assert.Equal(["github-token"], file["names"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("dev", file["vm"]!.GetValue<string>());
        Assert.Equal(30, file["size"]!.GetValue<long>());

        Assert.True((await h.HeartbeatAsync())["vaultScrub"]!.GetValue<bool>());
        var agent = (await JobsAsync(h)).Single()!;
        Assert.Equal("apply", agent["step"]!.GetValue<string>());
        var targets = agent["files"]!.AsArray();
        Assert.Equal(["/root/.claude/projects/-root-repo/abc.jsonl", "/root/.t3/userdata/state.sqlite"], targets.Select(f => Text(f!["path"])));
        Assert.All(targets, f => Assert.Equal("redact", f!["action"]!.GetValue<string>()));
        Assert.Contains("ghp_secret_value_1", targets[0]!["patterns"]!.AsArray().Select(Text));
        await PostAsync(h, agent["id"]!.GetValue<string>(), new
        {
            step = "apply",
            results = new object[]
            {
                new { path = targets[0]!["path"]!.GetValue<string>(), status = "ok", count = 2, detail = "" },
                new { path = targets[1]!["path"]!.GetValue<string>(), status = "partial", count = 1, detail = "wal" },
            },
        });
        activity = await ActivityAsync(h);
        Assert.Contains("Redacted github-token from 2 files of agent logs.", activity);
        Assert.Contains("Some data may stay in the write-ahead log of /root/.t3/userdata/state.sqlite until that app checkpoints it.", activity);
        Assert.Empty(await h.HeartbeatAsync());

        // The user's decision becomes an apply job; a deleted database would take its side files along.
        using (var decide = await h.Bob.PostAsJsonAsync($"/api/v1/vault/files/{file["id"]!.GetValue<string>()}", new { action = "delete" }))
            Assert.Equal(HttpStatusCode.NoContent, decide.StatusCode);
        Assert.Empty((await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/files"))!["files"]!.AsArray());
        var decided = (await JobsAsync(h)).Single()!;
        var delete = decided["files"]!.AsArray().Single()!;
        Assert.Equal("/root/repo/.env", Text(delete["path"]));
        Assert.Equal("delete", delete["action"]!.GetValue<string>());
        Assert.Empty(delete["patterns"]!.AsArray());
        await PostAsync(h, decided["id"]!.GetValue<string>(), new { step = "apply", results = new[] { new { path = delete["path"]!.GetValue<string>(), status = "ok", count = 1, detail = "" } } });
        Assert.Contains("Other files with github-token: 0 redacted, 1 deleted.", await ActivityAsync(h));
        Assert.Empty(await JobsAsync(h));
    }

    [Fact]
    public async Task Keep_redact_failures_and_an_incomplete_scan_are_recorded()
    {
        using var h = await VaultHarness.CreateAsync();
        await h.PutEntriesAsync(h.Entry("db", "pw-123456-long"));
        await h.ApprovedAsync(h.Request("request", ["db"]));
        await h.AnswerAsync(h.Request("release", ["db"]));
        h.App.Clock.Advance(TimeSpan.FromSeconds(6));

        var scan = (await JobsAsync(h)).Single()!;
        await PostAsync(h, scan["id"]!.GetValue<string>(), new { step = "scan", complete = false, hits = Array.Empty<object>() });
        Assert.Contains("Could not scrub db from the VM; retrying in 5 minutes.", await ActivityAsync(h));
        Assert.Empty(await JobsAsync(h));
        h.App.Clock.Advance(TimeSpan.FromMinutes(5));
        scan = (await JobsAsync(h)).Single()!;
        await PostAsync(h, scan["id"]!.GetValue<string>(), new
        {
            step = "scan", complete = true,
            hits = new object[]
            {
                new { path = B64("/root/a.txt"), indexes = new[] { 0 }, size = 10, type = "text" },
                new { path = B64("/root/b.db"), indexes = new[] { 0 }, size = 10, type = "sqlite" },
            },
        });
        var files = (await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/files"))!["files"]!.AsArray();
        Assert.Equal(2, files.Count);
        using (var keep = await h.Bob.PostAsJsonAsync($"/api/v1/vault/files/{files[0]!["id"]!.GetValue<string>()}", new { action = "keep" }))
            Assert.Equal(HttpStatusCode.NoContent, keep.StatusCode);
        using (var redact = await h.Bob.PostAsJsonAsync($"/api/v1/vault/files/{files[1]!["id"]!.GetValue<string>()}", new { action = "redact" }))
            Assert.Equal(HttpStatusCode.NoContent, redact.StatusCode);
        using (var gone = await h.Bob.PostAsJsonAsync($"/api/v1/vault/files/{files[1]!["id"]!.GetValue<string>()}", new { action = "redact" }))
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using (var invalid = await h.Bob.PostAsJsonAsync($"/api/v1/vault/files/{files[0]!["id"]!.GetValue<string>()}", new { action = "shred" }))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var job = (await JobsAsync(h)).Single()!;
        var target = job["files"]!.AsArray().Single()!;
        Assert.Equal("/root/b.db", Text(target["path"]));
        Assert.Equal(["pw-123456-long"], target["patterns"]!.AsArray().Select(Text));
        await PostAsync(h, job["id"]!.GetValue<string>(), new { step = "apply", results = new[] { new { path = target["path"]!.GetValue<string>(), status = "error", count = 0, detail = "locked" } } });
        var activity = await ActivityAsync(h);
        Assert.Contains("Kept /root/a.txt; it still holds db.", activity);
        Assert.Contains("Could not clean /root/b.db.", activity);

        // Results for an unknown job or the wrong step are refused.
        using (var unknown = await h.Guest.PostAsJsonAsync("/api/v1/vms/dev/vault/scrubs/0123456789abcdef", new { step = "scan", complete = true }))
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_locked_vms_scrub_waits_until_it_is_unlocked()
    {
        using var h = await VaultHarness.CreateAsync("locked");
        await h.PutEntriesAsync(h.Entry("db", "pw-123456-long"));
        using (var unlock = await h.Bob.PostAsJsonAsync("/api/v1/vms/dev/vault/unlock", new { key = h.KeyBase64 }))
            Assert.Equal(HttpStatusCode.NoContent, unlock.StatusCode);
        await h.ApprovedAsync(h.Request("request", ["db"]));
        await h.AnswerAsync(h.Request("release", ["db"]));
        h.Vault.Drop("dev");
        h.App.Clock.Advance(TimeSpan.FromSeconds(6));

        Assert.Empty(await h.HeartbeatAsync());
        Assert.Empty(await JobsAsync(h));
        using (var unlock = await h.Bob.PostAsJsonAsync("/api/v1/vms/dev/vault/unlock", new { key = h.KeyBase64 }))
            Assert.Equal(HttpStatusCode.NoContent, unlock.StatusCode);
        Assert.True((await h.HeartbeatAsync())["vaultScrub"]!.GetValue<bool>());
        Assert.Contains("pw-123456-long", (await JobsAsync(h)).Single()!["patterns"]!.AsArray().Select(p => Text(p!["pattern"])));
    }

    [Fact]
    public async Task Deleting_the_vm_forgets_its_vault_records()
    {
        using var h = await VaultHarness.CreateAsync();
        await h.PutEntriesAsync(h.Entry("db", "pw-123456-long"));
        await h.ApprovedAsync(h.Request("request", ["db"]));
        await h.PendingAsync(h.Request("delete", ["db"]));
        using (var delete = await h.Bob.DeleteAsync("/api/v1/vms/dev"))
            Assert.Equal(HttpStatusCode.Accepted, delete.StatusCode);
        Assert.Empty((await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/leases"))!["leases"]!.AsArray());
        Assert.Empty((await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/approvals"))!["approvals"]!.AsArray());
        // The entries are the user's, not the VM's.
        Assert.Single((await h.EntriesAsync())["entries"]!.AsArray());
    }
}
