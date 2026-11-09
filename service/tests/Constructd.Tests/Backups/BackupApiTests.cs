using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Constructd.Api.Backups;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Tests.Support;

namespace Constructd.Tests.Backups;

/// <summary>The host API of config backups (docs/plans/host-config-backups.md §3.4–§3.7).</summary>
public sealed class BackupApiTests
{
    private static string Code(JsonObject problem) => problem["code"]?.GetValue<string>() ?? "";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_backup_is_uploaded_streamed_listed_downloaded_and_its_keys_returned(bool sqlite)
    {
        using var h = await BackupHarness.CreateAsync(sqlite);
        var (status, opened) = await h.OpenAsync("manual", 4096);
        Assert.Equal(HttpStatusCode.Created, status);
        var id = opened["id"]!.GetValue<string>();
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Equal((100L << 30) - (5L << 30), opened["maxBytes"]!.GetValue<long>());

        var keys = BackupHarness.Keys(id, recipients: 2);
        Assert.Equal(HttpStatusCode.NoContent, await h.PutKeysAsync(id, keys));
        var payload = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
        var (uploaded, result) = await h.UploadAsync(id, payload, chunked: true);
        Assert.Equal(HttpStatusCode.OK, uploaded);
        var sha = Convert.ToHexStringLower(SHA256.HashData(payload));
        Assert.Equal(id, result["id"]!.GetValue<string>());
        Assert.Equal(payload.Length, result["sizeBytes"]!.GetValue<long>());
        Assert.Equal(sha, result["sha256"]!.GetValue<string>());
        Assert.Equal(h.App.Clock.UtcNow, result["createdAt"]!.GetValue<DateTimeOffset>());

        // <root>/<first 16 hex of sha256("alice")>/dev/<id>.age, nothing partial left.
        var file = Path.Combine(h.Root, BackupFileStore.OwnerSegment("alice"), "dev", id + ".age");
        Assert.Equal(payload, await File.ReadAllBytesAsync(file));
        Assert.Empty(h.Files("*.partial"));

        var listed = Assert.Single(await h.ListAsync());
        Assert.Equal(id, listed!["id"]!.GetValue<string>());
        Assert.Equal("manual", listed["trigger"]!.GetValue<string>());
        Assert.Equal(payload.Length, listed["sizeBytes"]!.GetValue<long>());
        Assert.Equal(sha, listed["sha256"]!.GetValue<string>());
        Assert.Null(listed["pinnedUntil"]);
        Assert.True(listed.AsObject().ContainsKey("pinnedUntil"));

        foreach (var client in new[] { h.Guest, h.Alice, h.Admin })
        {
            using var download = await client.GetAsync($"/api/v1/vms/dev/backups/{id}/content");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(payload.Length, download.Content.Headers.ContentLength);
            Assert.Equal("\"" + sha + "\"", download.Headers.ETag!.ToString());
            Assert.Equal("application/octet-stream", download.Content.Headers.ContentType!.MediaType);
            Assert.Equal(payload, await download.Content.ReadAsByteArrayAsync());
            using var keysResponse = await client.GetAsync($"/api/v1/vms/dev/backups/{id}/keys");
            Assert.Equal(HttpStatusCode.OK, keysResponse.StatusCode);
            Assert.Equal("application/json", keysResponse.Content.Headers.ContentType!.MediaType);
            Assert.Equal(keys, await keysResponse.Content.ReadAsStringAsync());
        }

        var statusView = await h.GetAsync("/status");
        Assert.True(statusView["enabled"]!.GetValue<bool>());
        Assert.Equal(1, statusView["count"]!.GetValue<int>());
        Assert.Equal(payload.Length, statusView["totalBytes"]!.GetValue<long>());
        Assert.Equal(h.App.Clock.UtcNow, statusView["lastSuccessAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(h.App.Clock.UtcNow, statusView["lastAttemptAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(h.App.Clock.UtcNow.AddHours(24), statusView["nextDueAt"]!.GetValue<DateTimeOffset>());
        Assert.Null(statusView["lastFailure"]);
        Assert.Null(statusView["failingSince"]);
        Assert.False(statusView["requestPending"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_upload_accepts_a_body_with_a_length_and_an_empty_unknown_id_route_is_not_matched()
    {
        using var h = await BackupHarness.CreateAsync();
        var id = await h.OpenIdAsync();
        Assert.Equal(HttpStatusCode.NoContent, await h.PutKeysAsync(id));
        var (status, body) = await h.UploadAsync(id, [1, 2, 3]);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3, body["sizeBytes"]!.GetValue<long>());
        // The id is 32 lowercase hex: anything else is not a backup route.
        Assert.Equal(HttpStatusCode.NotFound, (await h.Alice.GetAsync($"/api/v1/vms/dev/backups/{id.ToUpperInvariant()}/keys")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Alice.GetAsync("/api/v1/vms/dev/backups/xyz/keys")).StatusCode);
    }

    [Fact]
    public async Task Vm_token_owner_other_user_and_admin_each_reach_exactly_their_routes()
    {
        using var h = await BackupHarness.CreateAsync();
        var complete = await h.BackupAsync();
        var item = $"/api/v1/vms/dev/backups/{complete}";

        // The VM's own token: the guest routes and the reads, never the owner's routes.
        Assert.Equal(HttpStatusCode.OK, (await h.Guest.GetAsync("/api/v1/vms/dev/backups")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.Guest.GetAsync("/api/v1/vms/dev/backups/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Guest.PostAsync("/api/v1/vms/dev/backups/request", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Guest.GetAsync("/api/v1/vms/dev/backups/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Guest.PutAsJsonAsync("/api/v1/vms/dev/backups/settings", new { keep = 2 })).StatusCode);
        // ...and it may only abandon an open session, not delete a complete backup.
        Assert.Equal(HttpStatusCode.NotFound, (await h.Guest.DeleteAsync(item)).StatusCode);

        // Another VM's token reaches nothing of this VM.
        Assert.Equal(HttpStatusCode.Forbidden, (await h.OpenAsync(client: h.OtherGuest)).Status);
        foreach (var path in new[] { "", "/status", $"/{complete}/content", $"/{complete}/keys" })
            Assert.Equal(HttpStatusCode.Forbidden, (await h.OtherGuest.GetAsync("/api/v1/vms/dev/backups" + path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.OtherGuest.PostAsJsonAsync("/api/v1/vms/dev/backups/report",
            new { trigger = "scheduled", result = "failed", reason = "export-failed", detail = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.OtherGuest.DeleteAsync(item)).StatusCode);

        // The owner and an admin: reads and the owner's routes, never the guest's.
        foreach (var user in new[] { h.Alice, h.Admin })
        {
            foreach (var path in new[] { "", "/status", "/settings", $"/{complete}/content", $"/{complete}/keys" })
                Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/vms/dev/backups" + path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await h.OpenAsync(client: user)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await user.PutAsync(item + "/keys", BackupHarness.Json(BackupHarness.Keys(complete)))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await user.PutAsync(item + "/content", new ByteArrayContent([1]))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsync(item + "/restored", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/v1/vms/dev/backups/report",
                new { trigger = "scheduled", result = "failed", reason = "export-failed" })).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await user.PostAsync("/api/v1/vms/dev/backups/request", null)).StatusCode);
        }

        // Another user sees nothing of alice's VM.
        foreach (var path in new[] { "", "/status", "/settings", $"/{complete}/content", $"/{complete}/keys" })
            Assert.Equal(HttpStatusCode.Forbidden, (await h.Bob.GetAsync("/api/v1/vms/dev/backups" + path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Bob.PostAsync("/api/v1/vms/dev/backups/request", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Bob.PutAsJsonAsync("/api/v1/vms/dev/backups/settings", new { keep = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Bob.DeleteAsync(item)).StatusCode);

        // The owner deletes any backup; the admin too.
        Assert.Equal(HttpStatusCode.NoContent, (await h.Alice.DeleteAsync(item)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Alice.DeleteAsync(item)).StatusCode);
        var second = await h.BackupAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await h.Admin.DeleteAsync($"/api/v1/vms/dev/backups/{second}")).StatusCode);
        Assert.Empty(await h.ListAsync());
        Assert.Empty(h.Files("*"));
    }

    [Fact]
    public async Task The_guest_abandons_its_own_open_session_and_only_one_session_is_open_at_a_time()
    {
        using var h = await BackupHarness.CreateAsync();
        var id = await h.OpenIdAsync();
        var (busy, problem) = await h.OpenAsync();
        Assert.Equal(HttpStatusCode.Conflict, busy);
        Assert.Equal("backup-in-progress", Code(problem));
        Assert.Equal(HttpStatusCode.NoContent, (await h.Guest.DeleteAsync($"/api/v1/vms/dev/backups/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await h.OpenAsync()).Status);
        // An abandoned session (older than two hours, nothing writing) no longer blocks.
        h.App.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(HttpStatusCode.Created, (await h.OpenAsync()).Status);
        Assert.Single(await h.Store.ListAsync("alice", "dev", default), b => b.IsOpen);
    }

    [Fact]
    public async Task Keys_files_are_validated_and_only_accepted_for_an_open_session()
    {
        using var h = await BackupHarness.CreateAsync();
        var id = await h.OpenIdAsync();
        var valid = JsonNode.Parse(BackupHarness.Keys(id))!.AsObject();
        var cases = new (string Field, Action<JsonObject> Break)[]
        {
            ("format", k => k["format"] = "construct-backup-keys/2"),
            ("backupId", k => k["backupId"] = new string('0', 32)),
            ("createdAt", k => k["createdAt"] = "yesterday"),
            ("recipients", k => k["recipients"] = new JsonArray()),
            ("recipients", k => k["recipients"] = new JsonArray(Enumerable.Range(0, 33).Select(_ => (JsonNode)valid["recipients"]![0]!.DeepClone()).ToArray())),
            ("recipients.fingerprint", k => k["recipients"]![0]!["fingerprint"] = "ABC"),
            ("recipients.fingerprint", k => k["recipients"] = new JsonArray(valid["recipients"]![0]!.DeepClone(), valid["recipients"]![0]!.DeepClone())),
            ("recipients.label", k => k["recipients"]![0]!["label"] = "bad/label"),
            ("recipients.label", k => k["recipients"]![0]!["label"] = new string('x', 65)),
            ("recipients.wrapped", k => k["recipients"]![0]!["wrapped"] = "not base64!"),
        };
        foreach (var (field, change) in cases)
        {
            var keys = valid.DeepClone().AsObject();
            change(keys);
            using var response = await h.Guest.PutAsync($"/api/v1/vms/dev/backups/{id}/keys", BackupHarness.Json(keys.ToJsonString()));
            var problem = await BackupHarness.BodyAsync(response);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("validation", Code(problem));
            Assert.Equal(field, problem["field"]!.GetValue<string>());
        }
        using (var notJson = await h.Guest.PutAsync($"/api/v1/vms/dev/backups/{id}/keys", BackupHarness.Json("[1,2")))
            Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
        var huge = valid.DeepClone().AsObject();
        huge["padding"] = new string('x', 64 * 1024);
        using (var tooLarge = await h.Guest.PutAsync($"/api/v1/vms/dev/backups/{id}/keys", BackupHarness.Json(huge.ToJsonString())))
            Assert.Equal("validation", Code(await BackupHarness.BodyAsync(tooLarge)));

        // 32 recipients is the limit, not over it.
        Assert.Equal(HttpStatusCode.NoContent, await h.PutKeysAsync(id, BackupHarness.Keys(id, recipients: 32)));
        // A payload before keys is refused and ends the session.
        var second = await OpenAfterAsync(h, id);
        var (missing, problemMissing) = await h.UploadAsync(second, [1, 2, 3]);
        Assert.Equal(HttpStatusCode.Conflict, missing);
        Assert.Equal("backup-keys-missing", Code(problemMissing));
        Assert.Null(await h.Store.GetAsync(second, default));
        // Keys for a complete backup, or a backup that does not exist: not open.
        var complete = await h.BackupAsync();
        using (var late = await h.Guest.PutAsync($"/api/v1/vms/dev/backups/{complete}/keys", BackupHarness.Json(BackupHarness.Keys(complete))))
        {
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
            Assert.Equal("backup-not-open", Code(await BackupHarness.BodyAsync(late)));
        }
        var unknown = new string('a', 32);
        Assert.Equal(HttpStatusCode.Conflict, await h.PutKeysAsync(unknown, BackupHarness.Keys(unknown)));
        Assert.Equal(HttpStatusCode.Conflict, (await h.UploadAsync(unknown, [1])).Status);
    }

    private static async Task<string> OpenAfterAsync(BackupHarness h, string previous)
    {
        Assert.Equal(HttpStatusCode.NoContent, await h.AbandonAsync(previous));
        return await h.OpenIdAsync();
    }

    [Fact]
    public async Task An_upload_over_the_size_cap_is_refused_and_leaves_nothing_behind()
    {
        using var h = await BackupHarness.CreateAsync();
        h.Probe.Free = (5L << 30) + (1 << 20); // 1 MiB above the reserve
        var (status, opened) = await h.OpenAsync(estimatedBytes: 1000);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(1 << 20, opened["maxBytes"]!.GetValue<long>());
        var id = opened["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, await h.PutKeysAsync(id));

        // Streamed (no length): refused once the cap is crossed.
        var (tooLarge, problem) = await h.UploadAsync(id, new byte[(1 << 20) + 1], chunked: true);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge);
        Assert.Equal("backup-too-large", Code(problem));
        Assert.Equal(1 << 20, problem["maxBytes"]!.GetValue<long>());
        Assert.Null(await h.Store.GetAsync(id, default));
        Assert.Empty(h.Files());
        var failure = (await h.GetAsync("/status"))["lastFailure"]!;
        Assert.Equal("host-refused", failure["reason"]!.GetValue<string>());
        Assert.Equal("backup-too-large", failure["detail"]!.GetValue<string>());

        // With a length: refused before a byte is stored.
        var second = await h.OpenIdAsync();
        Assert.Equal(HttpStatusCode.NoContent, await h.PutKeysAsync(second));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await h.UploadAsync(second, new byte[(1 << 20) + 1])).Status);
        Assert.Null(await h.Store.GetAsync(second, default));
    }

    [Fact]
    public async Task An_upload_stops_when_the_host_falls_below_its_reserve()
    {
        using var h = await BackupHarness.CreateAsync();
        h.Coordinator.SpaceCheckBytes = 1 << 20;
        var id = await h.OpenIdAsync(estimatedBytes: 1000);
        Assert.Equal(HttpStatusCode.NoContent, await h.PutKeysAsync(id));
        var before = h.Probe.Calls;
        // Plenty at the start of the upload, then the disk fills up.
        h.Probe.ByCall = call => call <= before + 1 ? 100L << 30 : 1L << 30;
        var (status, problem) = await h.UploadAsync(id, RandomNumberGenerator.GetBytes(3 << 20), chunked: true);
        Assert.Equal((HttpStatusCode)507, status);
        Assert.Equal("host-disk-space", Code(problem));
        Assert.True(h.Probe.Calls >= before + 2);
        Assert.Null(await h.Store.GetAsync(id, default));
        Assert.Empty(h.Files("*"));
        var view = await h.GetAsync("/status");
        Assert.Equal("host-disk-space", view["lastFailure"]!["detail"]!.GetValue<string>());
        Assert.NotNull(view["failingSince"]);
    }

    [Fact]
    public async Task Deleting_a_session_cancels_its_upload_and_records_no_failure()
    {
        using var h = await BackupHarness.CreateAsync();
        h.Coordinator.SpaceCheckBytes = 1 << 20;
        var id = await h.OpenIdAsync();
        Assert.Equal(HttpStatusCode.NoContent, await h.PutKeysAsync(id));
        // The upload's first in-flight space check waits until the owner has deleted the session.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var inFlightCheck = h.Probe.Calls + 2;
        h.Probe.ByCall = call =>
        {
            if (call == inFlightCheck) { reached.SetResult(); release.Wait(TimeSpan.FromSeconds(30)); }
            return 100L << 30;
        };
        var upload = h.UploadAsync(id, RandomNumberGenerator.GetBytes(3 << 20), chunked: true);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(h.Coordinator.IsUploading(id));
        Assert.Single(h.Files("*.partial"));

        Assert.Equal(HttpStatusCode.NoContent, (await h.Alice.DeleteAsync($"/api/v1/vms/dev/backups/{id}")).StatusCode);
        release.Set();
        var (status, problem) = await upload;
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("backup-not-open", Code(problem));
        Assert.False(h.Coordinator.IsUploading(id));
        Assert.Empty(h.Files());
        Assert.Null(await h.Store.GetAsync(id, default));
        Assert.Null((await h.GetAsync("/status"))["lastFailure"]);
    }

    [Fact]
    public async Task Opening_a_session_checks_the_hosts_space_and_the_vms_quota()
    {
        using var h = await BackupHarness.CreateAsync();
        // free − max(estimate, last size) × 1.2 must stay ≥ the 5 GiB reserve.
        h.Probe.Free = (5L << 30) + 1200;
        Assert.Equal(HttpStatusCode.Created, (await h.OpenAsync(estimatedBytes: 1000)).Status);
        Assert.Equal(HttpStatusCode.NoContent, await h.AbandonAsync((await h.Store.ListAsync("alice", "dev", default)).Single().Id));
        var (full, problem) = await h.OpenAsync(estimatedBytes: 1001);
        Assert.Equal((HttpStatusCode)507, full);
        Assert.Equal("host-disk-space", Code(problem));

        h.Probe.Free = 100L << 30;
        await h.ConfigAsync(BackupHarness.Config(maxBytesPerVm: 64L << 20));
        var id = await h.BackupAsync(new byte[40 << 20]);
        // The last backup's size counts when it is larger than the estimate: 40 + 40 MiB > 64 MiB.
        var (quota, quotaProblem) = await h.OpenAsync(estimatedBytes: 1);
        Assert.Equal((HttpStatusCode)507, quota);
        Assert.Equal("backup-quota", Code(quotaProblem));
        Assert.Equal(HttpStatusCode.NoContent, (await h.Alice.DeleteAsync($"/api/v1/vms/dev/backups/{id}")).StatusCode);
        var (ok, opened) = await h.OpenAsync(estimatedBytes: 1);
        Assert.Equal(HttpStatusCode.Created, ok);
        Assert.Equal(64L << 20, opened["maxBytes"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_request_makes_the_heartbeat_ask_for_a_manual_backup_until_a_session_opens()
    {
        using var h = await BackupHarness.CreateAsync();
        // The first backup is due at once; take it so only the request remains.
        await h.BackupAsync();
        Assert.Empty(await h.HeartbeatAsync());

        using (var request = await h.Alice.PostAsync("/api/v1/vms/dev/backups/request", null))
        {
            Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
            Assert.True((await BackupHarness.BodyAsync(request))["requestPending"]!.GetValue<bool>());
        }
        Assert.True((await h.GetAsync("/status"))["requestPending"]!.GetValue<bool>());
        var due = await h.HeartbeatAsync(busy: true);
        Assert.True(due["backupDue"]!.GetValue<bool>());
        Assert.Equal("manual", due["backupTrigger"]!.GetValue<string>());
        Assert.False(due.ContainsKey("vaultScrub"));

        var id = await h.OpenIdAsync("manual");
        Assert.Empty(await h.HeartbeatAsync());
        Assert.False((await h.GetAsync("/status"))["requestPending"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NoContent, await h.PutKeysAsync(id));
        Assert.Equal(HttpStatusCode.OK, (await h.UploadAsync(id, [1, 2, 3])).Status);
        Assert.Empty(await h.HeartbeatAsync());

        // A request nobody answers expires after an hour.
        Assert.Equal(HttpStatusCode.Accepted, (await h.Alice.PostAsync("/api/v1/vms/dev/backups/request", null)).StatusCode);
        h.App.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(await h.HeartbeatAsync());
        Assert.False((await h.GetAsync("/status"))["requestPending"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_open_ssh_connection_alone_does_not_hold_back_a_scheduled_backup()
    {
        using var h = await BackupHarness.CreateAsync();
        // Agent work holds it back...
        Assert.Empty(await h.HeartbeatAsync(busy: true, reasons: ["ssh-session", "agent-log:claude"]));
        // ...the client's notification watch (an SSH connection and nothing else) does not.
        var due = await h.HeartbeatAsync(busy: true, reasons: ["ssh-session"]);
        Assert.Equal("scheduled", due["backupTrigger"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_scheduled_backup_waits_for_an_idle_heartbeat_and_the_interval()
    {
        using var h = await BackupHarness.CreateAsync();
        Assert.Empty(await h.HeartbeatAsync(busy: true));
        var due = await h.HeartbeatAsync(busy: false);
        Assert.Equal("scheduled", due["backupTrigger"]!.GetValue<string>());
        Assert.Equal(2, due.Count);

        await h.BackupAsync();
        h.App.Clock.Advance(TimeSpan.FromHours(23));
        Assert.Empty(await h.HeartbeatAsync());
        h.App.Clock.Advance(TimeSpan.FromHours(1));
        Assert.True((await h.HeartbeatAsync())["backupDue"]!.GetValue<bool>());

        // A guest that skipped reports it; the next try waits min(interval, 6 h).
        using (var report = await h.Guest.PostAsJsonAsync("/api/v1/vms/dev/backups/report",
                   new { trigger = "scheduled", result = "skipped", reason = "vm-disk-space", detail = "need=1 free=0 reserve=2" }))
            Assert.Equal(HttpStatusCode.NoContent, report.StatusCode);
        Assert.Empty(await h.HeartbeatAsync());
        h.App.Clock.Advance(TimeSpan.FromHours(6));
        Assert.True((await h.HeartbeatAsync())["backupDue"]!.GetValue<bool>());

        // Children never get one; a disabled host neither.
        await h.ConfigAsync(BackupHarness.Config(enabled: false));
        Assert.Empty(await h.HeartbeatAsync());
    }

    [Fact]
    public async Task Reports_record_the_failure_streak_and_a_success_ends_it()
    {
        using var h = await BackupHarness.CreateAsync();
        var first = h.App.Clock.UtcNow;
        Assert.Equal(HttpStatusCode.NoContent, (await h.Guest.PostAsJsonAsync("/api/v1/vms/dev/backups/report",
            new { trigger = "scheduled", result = "failed", reason = "no-recipients", detail = (string?)null })).StatusCode);
        h.App.Clock.Advance(TimeSpan.FromHours(7));
        Assert.Equal(HttpStatusCode.NoContent, (await h.Guest.PostAsJsonAsync("/api/v1/vms/dev/backups/report",
            new { trigger = "manual", result = "failed", reason = "export-failed", detail = "tar exited 2" })).StatusCode);
        var view = await h.GetAsync("/status");
        Assert.Equal(first, view["failingSince"]!.GetValue<DateTimeOffset>());
        var failure = view["lastFailure"]!.AsObject();
        Assert.Equal(h.App.Clock.UtcNow, failure["at"]!.GetValue<DateTimeOffset>());
        Assert.Equal("manual", failure["trigger"]!.GetValue<string>());
        Assert.Equal("export-failed", failure["reason"]!.GetValue<string>());
        Assert.Equal("tar exited 2", failure["detail"]!.GetValue<string>());
        Assert.Equal(h.App.Clock.UtcNow, view["lastAttemptAt"]!.GetValue<DateTimeOffset>());

        await h.BackupAsync();
        view = await h.GetAsync("/status");
        Assert.Null(view["failingSince"]);
        Assert.Equal("export-failed", view["lastFailure"]!["reason"]!.GetValue<string>());

        foreach (var invalid in new object[]
                 {
                     new { trigger = "hourly", result = "failed", reason = "x" },
                     new { trigger = "manual", result = "ok", reason = "x" },
                     new { trigger = "manual", result = "failed", reason = "Bad Reason" },
                     new { trigger = "manual", result = "failed", reason = "x", detail = new string('d', 501) },
                 })
        {
            using var response = await h.Guest.PostAsJsonAsync("/api/v1/vms/dev/backups/report", invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("validation", Code(await BackupHarness.BodyAsync(response)));
        }
    }

    [Fact]
    public async Task A_pre_reinstall_backup_is_pinned_until_restored_and_survives_rotation_meanwhile()
    {
        using var h = await BackupHarness.CreateAsync();
        await h.ConfigAsync(BackupHarness.Config(keep: 1));
        var pinned = await h.BackupAsync(trigger: "pre-reinstall");
        var created = h.App.Clock.UtcNow;
        Assert.Equal(created.AddDays(14), (await h.ListAsync())[0]!["pinnedUntil"]!.GetValue<DateTimeOffset>());

        // The empty VM's first scheduled backups do not rotate it out.
        h.App.Clock.Advance(TimeSpan.FromMinutes(5));
        var first = await h.BackupAsync(trigger: "scheduled");
        h.App.Clock.Advance(TimeSpan.FromMinutes(5));
        var second = await h.BackupAsync(trigger: "scheduled");
        Assert.Equal([second, pinned], (await h.ListAsync()).Select(b => b!["id"]!.GetValue<string>()));
        Assert.DoesNotContain(first, (await h.ListAsync()).Select(b => b!["id"]!.GetValue<string>()));

        Assert.Equal(HttpStatusCode.NoContent, (await h.Guest.PostAsync($"/api/v1/vms/dev/backups/{pinned}/restored", null)).StatusCode);
        Assert.Null((await h.ListAsync()).Single(b => b!["id"]!.GetValue<string>() == pinned)!["pinnedUntil"]);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Guest.PostAsync($"/api/v1/vms/dev/backups/{new string('b', 32)}/restored", null)).StatusCode);

        // Unpinned, the next rotation treats it like any other backup.
        h.App.Clock.Advance(TimeSpan.FromMinutes(5));
        var third = await h.BackupAsync(trigger: "scheduled");
        Assert.Equal([third], (await h.ListAsync()).Select(b => b!["id"]!.GetValue<string>()));
        Assert.Single(h.Files("*.age"));
    }

    [Fact]
    public async Task Settings_override_the_host_defaults_within_its_limits()
    {
        using var h = await BackupHarness.CreateAsync();
        var view = await h.GetAsync("/settings");
        Assert.Null(view["intervalHours"]);
        Assert.Null(view["keep"]);
        Assert.Equal("""{"enabled":true,"intervalHours":24,"keep":4}""", view["effective"]!.ToJsonString());
        Assert.Equal("""{"enabled":true,"intervalHours":24,"keep":4,"maxKeep":10}""", view["host"]!.ToJsonString());

        using (var put = await h.Alice.PutAsJsonAsync("/api/v1/vms/dev/backups/settings", new { intervalHours = 0, keep = 10 }))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            var updated = await BackupHarness.BodyAsync(put);
            Assert.Equal(0, updated["intervalHours"]!.GetValue<int>());
            Assert.Equal("""{"enabled":true,"intervalHours":0,"keep":10}""", updated["effective"]!.ToJsonString());
        }
        // Scheduled backups are off for this VM; a request still works.
        Assert.Empty(await h.HeartbeatAsync());
        Assert.Null((await h.GetAsync("/status"))["nextDueAt"]);
        Assert.Equal(HttpStatusCode.Accepted, (await h.Alice.PostAsync("/api/v1/vms/dev/backups/request", null)).StatusCode);
        Assert.Equal("manual", (await h.HeartbeatAsync())["backupTrigger"]!.GetValue<string>());

        foreach (var (body, field) in new (object, string)[]
                 {
                     (new { intervalHours = 721 }, "intervalHours"), (new { intervalHours = -1 }, "intervalHours"),
                     (new { keep = 0 }, "keep"), (new { keep = 11 }, "keep"), (new { keep = 1.5 }, "keep"),
                     (new { intervalHours = "24" }, "intervalHours"), (new { every = 3 }, "every"),
                 })
        {
            using var response = await h.Alice.PutAsJsonAsync("/api/v1/vms/dev/backups/settings", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await BackupHarness.BodyAsync(response);
            Assert.Equal("validation", Code(problem));
            Assert.Equal(field, problem["field"]!.GetValue<string>());
        }

        // null returns to the host default; a lower host maximum caps a stored override.
        Assert.Equal(HttpStatusCode.OK, (await h.Alice.PutAsJsonAsync("/api/v1/vms/dev/backups/settings", new { intervalHours = (int?)null, keep = 9 })).StatusCode);
        await h.ConfigAsync(BackupHarness.Config(keep: 2, maxKeep: 3, intervalHours: 12));
        view = await h.GetAsync("/settings");
        Assert.Equal(9, view["keep"]!.GetValue<int>());
        Assert.Equal("""{"enabled":true,"intervalHours":12,"keep":3}""", view["effective"]!.ToJsonString());
        Assert.Equal("""{"enabled":true,"intervalHours":12,"keep":2,"maxKeep":3}""", view["host"]!.ToJsonString());
    }

    [Fact]
    public async Task The_host_config_section_is_validated_and_disabling_refuses_new_backups()
    {
        using var h = await BackupHarness.CreateAsync();
        var config = (await h.Admin.GetFromJsonAsync<JsonObject>("/api/v1/host/config"))!["backups"]!.AsObject();
        Assert.True(config["enabled"]!.GetValue<bool>());
        Assert.Equal(24, config["intervalHours"]!.GetValue<int>());
        Assert.Equal(4, config["keep"]!.GetValue<int>());
        Assert.Equal(10, config["maxKeep"]!.GetValue<int>());
        Assert.Equal(14, config["pinDays"]!.GetValue<int>());
        Assert.Equal(14, config["retainDeletedDays"]!.GetValue<int>());
        Assert.Equal(5L << 30, config["minFreeBytes"]!.GetValue<long>());
        Assert.Null(config["maxBytesPerVm"]);
        Assert.Equal("default", config["source"]!.GetValue<string>());

        foreach (var invalid in new[]
                 {
                     BackupHarness.Config(intervalHours: 721), BackupHarness.Config(intervalHours: -1), BackupHarness.Config(keep: 0),
                     BackupHarness.Config(keep: 5, maxKeep: 4), BackupHarness.Config(keep: 4, maxKeep: 51), BackupHarness.Config(pinDays: 0),
                     BackupHarness.Config(pinDays: 366), BackupHarness.Config(retainDeletedDays: -1), BackupHarness.Config(retainDeletedDays: 3651),
                     BackupHarness.Config(minFreeBytes: -1), BackupHarness.Config(maxBytesPerVm: (64L << 20) - 1),
                 })
        {
            using var response = await h.Admin.PutAsJsonAsync("/api/v1/host/config", new { backups = invalid });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        await h.ConfigAsync(BackupHarness.Config(intervalHours: 0, keep: 50, maxKeep: 50, pinDays: 365, retainDeletedDays: 0,
            minFreeBytes: 0, maxBytesPerVm: 64L << 20));

        await h.ConfigAsync(BackupHarness.Config(enabled: false));
        var (status, problem) = await h.OpenAsync();
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("backups-disabled", Code(problem));
        using (var request = await h.Alice.PostAsync("/api/v1/vms/dev/backups/request", null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, request.StatusCode);
            Assert.Equal("backups-disabled", Code(await BackupHarness.BodyAsync(request)));
        }
        var view = await h.GetAsync("/status");
        Assert.False(view["enabled"]!.GetValue<bool>());
        Assert.Null(view["nextDueAt"]);
    }

    [Fact]
    public async Task Backups_belong_to_owner_and_vm_name_and_survive_the_vm()
    {
        using var h = await BackupHarness.CreateAsync();
        var id = await h.BackupAsync();
        await BackupHarness.DeleteVmAsync(h.Alice, "dev");
        Assert.NotNull(await h.Store.GetAsync(id, default));

        // Another owner reusing the name sees none of them.
        using var carol = await h.App.CreateUserClientAsync("carol");
        await carol.CreateVmAsync("dev");
        Assert.Empty((await carol.GetFromJsonAsync<JsonObject>("/api/v1/vms/dev/backups"))!["backups"]!.AsArray());
        Assert.Equal(0, (await carol.GetFromJsonAsync<JsonObject>("/api/v1/vms/dev/backups/status"))!["count"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.NotFound, (await carol.GetAsync($"/api/v1/vms/dev/backups/{id}/keys")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await carol.DeleteAsync($"/api/v1/vms/dev/backups/{id}")).StatusCode);
        await BackupHarness.DeleteVmAsync(carol, "dev");

        // The same owner's new VM of that name sees its predecessor's backups.
        await h.Alice.CreateVmAsync("dev");
        Assert.Equal(id, (await h.ListAsync()).Single()!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Child_vms_have_no_backups()
    {
        using var h = await BackupHarness.CreateAsync();
        await h.App.Vms.AddAsync(new Vm("kid", "alice", 2, 4, 32, h.App.Clock.UtcNow, VmState.Running, null, null,
            new IdlePolicy(60, IdleAction.Save), Vm.NoForwards, Kind: VmKind.Child, Parent: "dev"), 10, default);
        using var response = await h.Alice.GetAsync("/api/v1/vms/kid/backups/status");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not-a-primary", Code(await BackupHarness.BodyAsync(response)));
        // A child's heartbeat never asks for a backup.
        Assert.Empty(await h.HeartbeatAsync(client: h.Alice, vm: "kid"));
    }

    [Fact]
    public async Task Mutations_are_audited_with_ids_sizes_and_codes_but_never_content()
    {
        using var h = await BackupHarness.CreateAsync();
        var id = await h.BackupAsync(new byte[1234]);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Guest.PostAsJsonAsync("/api/v1/vms/dev/backups/report",
            new { trigger = "scheduled", result = "failed", reason = "export-failed", detail = "secret-looking detail" })).StatusCode);
        var entries = await h.App.Service<IAuditLog>().QueryAsync(200, default);
        var actions = entries.Select(e => e.Action).ToHashSet();
        Assert.Superset(new HashSet<string> { "backup.create", "backup.keys", "backup.upload", "backup.report" }, actions);
        var upload = entries.Single(e => e.Action == "backup.upload");
        Assert.Contains("target=" + id, upload.Detail);
        Assert.Contains("sizeBytes=1234", upload.Detail);
        Assert.DoesNotContain(entries, e => e.Detail?.Contains("secret-looking", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(entries, e => e.Detail?.Contains("wrapped", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task The_heartbeat_reply_carries_a_vault_scrub_and_a_backup_together_or_alone()
    {
        using var h = await Constructd.Tests.Vault.VaultHarness.CreateAsync();
        using var admin = await h.App.CreateUserClientAsync("admin", Role.Admin);
        await h.PutEntriesAsync(h.Entry("github-token", "ghp_secret_value_1", "bot"));
        await h.ApprovedAsync(h.Request("request", ["github-token"], new { ttl = 600 }));

        var reply = await h.HeartbeatAsync();
        Assert.Equal(["backupDue", "backupTrigger"], reply.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal("scheduled", reply["backupTrigger"]!.GetValue<string>());

        h.App.Clock.Advance(TimeSpan.FromMinutes(11));
        await h.Vault.TickAsync(default);
        reply = await h.HeartbeatAsync();
        Assert.Equal(["backupDue", "backupTrigger", "vaultScrub"], reply.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.True(reply["vaultScrub"]!.GetValue<bool>());
        Assert.True(reply["backupDue"]!.GetValue<bool>());

        using (var off = await admin.PutAsJsonAsync("/api/v1/host/config", new { backups = BackupHarness.Config(enabled: false) }))
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        reply = await h.HeartbeatAsync();
        Assert.Equal(["vaultScrub"], reply.Select(p => p.Key));
    }

    [Fact]
    public async Task The_feature_is_advertised_on_both_platforms_and_paused_while_the_host_drains()
    {
        Assert.Contains("config-backups", new Constructd.Api.Composition.ReleaseInfo().ApiFeatures);
        Assert.Contains("config-backups", new Constructd.Api.Composition.ReleaseInfo(
            new Constructd.Core.Configuration.ConstructdOptions { Backend = "proxmox" }).ApiFeatures);
        using var h = await BackupHarness.CreateAsync();
        var health = (await h.App.CreateAnonymousClient().GetFromJsonAsync<JsonObject>("/api/v1/health"))!;
        Assert.Contains("config-backups", health["apiFeatures"]!.AsArray().Select(n => n!.GetValue<string>()));

        var gate = h.App.Service<IMaintenanceGate>();
        gate.Enter(MaintenanceState.Draining, "update");
        Assert.Empty(await h.HeartbeatAsync());
        using (var response = await h.Guest.PostAsJsonAsync("/api/v1/vms/dev/backups", new { trigger = "scheduled", estimatedBytes = 1 }))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("maintenance", Code(await BackupHarness.BodyAsync(response)));
        }
        gate.Reopen();
        Assert.True((await h.HeartbeatAsync())["backupDue"]!.GetValue<bool>());
    }
}
