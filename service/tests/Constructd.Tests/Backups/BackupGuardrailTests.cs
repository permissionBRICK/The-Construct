using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Constructd.Core.Domain;

namespace Constructd.Tests.Backups;

/// <summary>
/// The backups guardrail (docs/plans/construct-platform-design.md 10.2, Appendix C.6) through the API: the host's
/// one open request per VM, the VM's own slot, separate budgets, and what a VM token can never do.
/// </summary>
public sealed class BackupGuardrailTests
{
    private static string Code(JsonObject problem) => problem["code"]?.GetValue<string>() ?? "";

    private static async Task<JsonObject> StatusAsync(BackupHarness h) => await h.GetAsync("/status");

    private static async Task<HttpStatusCode> ReportAsync(BackupHarness h, string trigger = "manual", string reason = "export-failed")
    {
        using var response = await h.Guest.PostAsJsonAsync("/api/v1/vms/dev/backups/report", new { trigger, result = "failed", reason, detail = "" });
        return response.StatusCode;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task One_host_request_accepts_one_upload_as_scheduled_and_a_second_create_in_the_hour_is_a_slot(bool sqlite)
    {
        using var h = await BackupHarness.CreateAsync(sqlite);
        Assert.Equal("scheduled", (await h.HeartbeatAsync())["backupTrigger"]!.GetValue<string>());
        // The guest says "pre-reinstall"; the host's request says scheduled, and that is what it is.
        var (status, opened) = await h.OpenAsync("pre-reinstall", requested: false);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("scheduled", opened["kind"]!.GetValue<string>());
        var first = opened["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, await h.PutKeysAsync(first));
        var (uploaded, result) = await h.UploadAsync(first, [1, 2, 3]);
        Assert.Equal(HttpStatusCode.OK, uploaded);
        Assert.Equal("scheduled", result["kind"]!.GetValue<string>());

        // Within the same hour the request is gone: the next create is the VM's slot, never pinned.
        h.App.Clock.Advance(TimeSpan.FromMinutes(10));
        var second = await h.BackupAsync(trigger: "pre-reinstall", requested: false);
        var list = await h.ListAsync();
        var slot = list.Single(b => b!["id"]!.GetValue<string>() == second)!;
        Assert.Equal("slot", slot["kind"]!.GetValue<string>());
        Assert.Equal("slot", slot["trigger"]!.GetValue<string>());
        Assert.Null(slot["pinnedUntil"]);
        Assert.False(slot["protected"]!.GetValue<bool>());
        var scheduled = list.Single(b => b!["id"]!.GetValue<string>() == first)!;
        Assert.Equal("scheduled", scheduled["kind"]!.GetValue<string>());
        Assert.True(scheduled["protected"]!.GetValue<bool>());
        Assert.Null(scheduled["pinnedUntil"]);
    }

    [Fact]
    public async Task Slot_uploads_never_change_schedule_state()
    {
        using var h = await BackupHarness.CreateAsync();
        await h.BackupAsync(trigger: "scheduled");
        var before = await StatusAsync(h);
        var state = await h.Store.GetVmAsync("alice", "dev", default);

        // A slot upload, a failed one, an abandoned one and a report about it, an hour apart.
        h.App.Clock.Advance(TimeSpan.FromHours(1));
        await h.BackupAsync(requested: false);
        h.App.Clock.Advance(TimeSpan.FromHours(1));
        var failing = await h.OpenIdAsync(requested: false);
        Assert.Equal(HttpStatusCode.Conflict, (await h.UploadAsync(failing, [1])).Status); // no keys: refused, session closed
        Assert.Equal(HttpStatusCode.NoContent, await ReportAsync(h));
        h.App.Clock.Advance(TimeSpan.FromHours(1));
        var abandoned = await h.OpenIdAsync(requested: false);
        Assert.Equal(HttpStatusCode.NoContent, await h.AbandonAsync(abandoned));

        var after = await StatusAsync(h);
        foreach (var field in new[] { "lastAttemptAt", "lastSuccessAt", "lastFailure", "failingSince", "requestPending", "nextDueAt" })
            Assert.True(JsonNode.DeepEquals(before[field], after[field]), field);
        var stateAfter = (await h.Store.GetVmAsync("alice", "dev", default))!;
        Assert.Equal(state! with { SlotLastAt = stateAfter.SlotLastAt }, stateAfter);
        // The schedule still asks at its own time, not earlier and not later.
        h.App.Clock.Advance(TimeSpan.FromHours(21) - TimeSpan.FromMinutes(1));
        Assert.Empty(await h.HeartbeatAsync());
        h.App.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("scheduled", (await h.HeartbeatAsync())["backupTrigger"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_full_slot_never_prevents_a_host_requested_upload_within_the_quota()
    {
        using var h = await BackupHarness.CreateAsync(slotMaxBytes: 1 << 20);
        await h.ConfigAsync(BackupHarness.Config(maxBytesPerVm: 64L << 20));
        // The slot is full: its content is as large as it may be, and a larger one is refused.
        var slot = await h.BackupAsync(new byte[1 << 20], requested: false);
        h.App.Clock.Advance(TimeSpan.FromMinutes(30));
        var (tooBig, problem) = await h.OpenAsync(estimatedBytes: (1 << 20) + 1, requested: false);
        Assert.Equal((HttpStatusCode)507, tooBig);
        Assert.Equal("backup-quota", Code(problem));

        // The host's request still gets its upload, with the whole quota: the slot is not counted there.
        var (status, opened) = await h.OpenAsync("scheduled", estimatedBytes: 60L << 20);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(64L << 20, opened["maxBytes"]!.GetValue<long>());
        var id = opened["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, await h.PutKeysAsync(id));
        Assert.Equal(HttpStatusCode.OK, (await h.UploadAsync(id, new byte[60 << 20])).Status);
        Assert.Equal([id, slot], (await h.ListAsync()).Select(b => b!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_stalled_slot_upload_never_prevents_a_host_requested_upload()
    {
        using var h = await BackupHarness.CreateAsync();
        h.Coordinator.SpaceCheckBytes = 1 << 20;
        var slot = await h.OpenIdAsync(requested: false);
        Assert.Equal(HttpStatusCode.NoContent, await h.PutKeysAsync(slot));
        // The slot upload stalls in flight (at its first space check) and stays there.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var inFlightCheck = h.Probe.Calls + 2;
        h.Probe.ByCall = call =>
        {
            if (call == inFlightCheck) { reached.SetResult(); release.Wait(TimeSpan.FromSeconds(30)); }
            return 100L << 30;
        };
        var stalled = h.UploadAsync(slot, RandomNumberGenerator.GetBytes(3 << 20), chunked: true);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(h.Coordinator.IsUploading(slot));

        // A stalled slot session never holds the heartbeat back, and the host's request opens over it.
        Assert.Equal("scheduled", (await h.HeartbeatAsync())["backupTrigger"]!.GetValue<string>());
        var (status, opened) = await h.OpenAsync("manual", requested: false);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("scheduled", opened["kind"]!.GetValue<string>());
        Assert.Null(await h.Store.GetAsync(slot, default));
        release.Set();
        Assert.Equal(HttpStatusCode.Conflict, (await stalled).Status);

        var id = opened["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, await h.PutKeysAsync(id));
        Assert.Equal(HttpStatusCode.OK, (await h.UploadAsync(id, [1, 2, 3])).Status);
        Assert.Equal(id, (await h.ListAsync()).Single()!["id"]!.GetValue<string>());
        Assert.Equal(h.App.Clock.UtcNow, (await StatusAsync(h))["lastSuccessAt"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task The_slot_is_replaced_after_verification_at_most_every_30_minutes_and_expires_when_idle()
    {
        using var h = await BackupHarness.CreateAsync(slotMaxBytes: 1 << 20);
        var first = await h.BackupAsync(requested: false);
        var (tooSoon, problem) = await h.OpenAsync(requested: false);
        Assert.Equal((HttpStatusCode)429, tooSoon);
        Assert.Equal("slot-too-soon", Code(problem));
        Assert.Equal(30 * 60, problem["retryAfterSeconds"]!.GetValue<long>());

        // A failed slot upload leaves the previous slot content as it was.
        h.App.Clock.Advance(TimeSpan.FromMinutes(30));
        var (opened, body) = await h.OpenAsync(requested: false);
        Assert.Equal(HttpStatusCode.Created, opened);
        Assert.Equal(1 << 20, body["maxBytes"]!.GetValue<long>());
        var failing = body["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, await h.PutKeysAsync(failing));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await h.UploadAsync(failing, new byte[(1 << 20) + 1], chunked: true)).Status);
        Assert.Equal([first], (await h.ListAsync()).Select(b => b!["id"]!.GetValue<string>()));
        Assert.Null((await StatusAsync(h))["lastFailure"]);

        // A complete one replaces it.
        h.App.Clock.Advance(TimeSpan.FromMinutes(30));
        var second = await h.BackupAsync(requested: false);
        Assert.Equal([second], (await h.ListAsync()).Select(b => b!["id"]!.GetValue<string>()));
        Assert.Single(h.Files("*.age"));

        // A slot session nobody uploads into expires after 30 minutes; until then it blocks the next one.
        h.App.Clock.Advance(TimeSpan.FromMinutes(30));
        var idle = await h.OpenIdAsync(requested: false);
        h.App.Clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(HttpStatusCode.Conflict, (await h.OpenAsync(requested: false)).Status);
        h.App.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Created, (await h.OpenAsync(requested: false)).Status);
        Assert.Null(await h.Store.GetAsync(idle, default));
    }

    [Fact]
    public async Task A_host_request_cancels_an_open_slot_session_and_a_host_session_blocks_the_slot()
    {
        using var h = await BackupHarness.CreateAsync();
        var slot = await h.OpenIdAsync(requested: false);
        var host = await h.OpenIdAsync("scheduled");
        Assert.Null(await h.Store.GetAsync(slot, default));
        h.App.Clock.Advance(TimeSpan.FromMinutes(30));
        var (busy, problem) = await h.OpenAsync(requested: false);
        Assert.Equal(HttpStatusCode.Conflict, busy);
        Assert.Equal("backup-in-progress", Code(problem));
        // A second request while the host's session is open does not get a second one, and stays open.
        var (again, _) = await h.OpenAsync("manual");
        Assert.Equal(HttpStatusCode.Conflict, again);
        Assert.True((await StatusAsync(h))["requestPending"]!.GetValue<bool>());
        Assert.Equal(BackupOrigins.Host, (await h.Store.GetAsync(host, default))!.Origin);
    }

    [Fact]
    public async Task Budgets_are_separate_the_slot_is_not_counted_in_the_quota()
    {
        using var h = await BackupHarness.CreateAsync(slotMaxBytes: 64L << 20);
        await h.ConfigAsync(BackupHarness.Config(maxBytesPerVm: 64L << 20));
        await h.BackupAsync(new byte[40 << 20]);
        // The quota of host-requested backups is too full for another 40 MiB, the slot is not.
        var (quota, problem) = await h.OpenAsync(estimatedBytes: 1);
        Assert.Equal((HttpStatusCode)507, quota);
        Assert.Equal("backup-quota", Code(problem));
        // The refused create consumed the request; the next create is a slot within its own budget.
        var (slot, opened) = await h.OpenAsync(estimatedBytes: 1, requested: false);
        Assert.Equal(HttpStatusCode.Created, slot);
        Assert.Equal(64L << 20, opened["maxBytes"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_vm_token_deletes_pins_and_unpins_nothing()
    {
        using var h = await BackupHarness.CreateAsync();
        var host = await h.BackupAsync(trigger: "scheduled");
        h.App.Clock.Advance(TimeSpan.FromMinutes(5));
        var slot = await h.BackupAsync(requested: false);
        foreach (var id in new[] { host, slot })
        {
            var item = $"/api/v1/vms/dev/backups/{id}";
            Assert.Equal(HttpStatusCode.NotFound, (await h.Guest.DeleteAsync(item)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await h.Guest.DeleteAsync(item + "?confirm=true")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await h.Guest.PostAsync(item + "/pin", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await h.Guest.DeleteAsync(item + "/pin")).StatusCode);
        }
        Assert.Equal(2, (await h.ListAsync()).Count);

        // The user pins a host-requested backup (not the slot), and the pin keeps it through rotation.
        Assert.Equal(HttpStatusCode.NoContent, (await h.Alice.PostAsync($"/api/v1/vms/dev/backups/{host}/pin", null)).StatusCode);
        using (var slotPin = await h.Alice.PostAsync($"/api/v1/vms/dev/backups/{slot}/pin", null))
        {
            Assert.Equal(HttpStatusCode.Conflict, slotPin.StatusCode);
            Assert.Equal("backup-is-slot", Code(await BackupHarness.BodyAsync(slotPin)));
        }
        var pinned = (await h.ListAsync()).Single(b => b!["id"]!.GetValue<string>() == host)!;
        Assert.True(pinned["pinned"]!.GetValue<bool>());
        Assert.Equal(h.App.Clock.UtcNow.AddDays(14), pinned["pinnedUntil"]!.GetValue<DateTimeOffset>());
        // The guest's restore report does not release it.
        Assert.Equal(HttpStatusCode.NoContent, (await h.Guest.PostAsync($"/api/v1/vms/dev/backups/{host}/restored", null)).StatusCode);
        Assert.True((await h.ListAsync()).Single(b => b!["id"]!.GetValue<string>() == host)!["pinned"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Existing_backups_without_a_mark_count_as_host_requested_and_old_list_fields_stay()
    {
        using var h = await BackupHarness.CreateAsync(sqlite: true);
        var now = h.App.Clock.UtcNow;
        // A backup stored by the previous release: a VM-chosen label and no origin.
        var legacy = new ConfigBackup(new string('e', 32), "alice", "dev", BackupTriggers.PreReinstall, BackupStates.Complete,
            now.AddDays(-3), now.AddDays(-3), 3, new string('f', 64), KeysJson: "{}");
        await h.Store.InsertAsync(legacy, default);

        var listed = (await h.ListAsync()).Single()!.AsObject();
        foreach (var field in new[] { "id", "createdAt", "trigger", "sizeBytes", "sha256", "pinnedUntil" }) Assert.True(listed.ContainsKey(field), field);
        Assert.Equal("pre-reinstall", listed["trigger"]!.GetValue<string>());
        Assert.Equal("pre-reinstall", listed["kind"]!.GetValue<string>());
        Assert.True(listed["protected"]!.GetValue<bool>());

        // A slot upload does not replace it, the VM cannot delete it, rotation keeps it.
        await h.BackupAsync(requested: false);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Guest.DeleteAsync($"/api/v1/vms/dev/backups/{legacy.Id}")).StatusCode);
        await h.ConfigAsync(BackupHarness.Config(keep: 1));
        h.App.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.BackupAsync(trigger: "scheduled");
        h.App.Clock.Advance(TimeSpan.FromMinutes(1));
        var newest = await h.BackupAsync(trigger: "scheduled");
        var ids = (await h.ListAsync()).Select(b => b!["id"]!.GetValue<string>()).ToArray();
        Assert.Equal(3, ids.Length);
        Assert.Equal(newest, ids[0]);
        Assert.Equal(legacy.Id, ids[^1]);
    }

    [Fact]
    public async Task A_report_without_an_open_request_or_attempt_changes_nothing()
    {
        using var h = await BackupHarness.CreateAsync();
        await h.BackupAsync(trigger: "scheduled");
        h.App.Clock.Advance(TimeSpan.FromHours(3));
        var before = await StatusAsync(h);
        Assert.Equal(HttpStatusCode.NoContent, await ReportAsync(h, "scheduled"));
        var after = await StatusAsync(h);
        Assert.True(JsonNode.DeepEquals(before, after));

        // Answering an open request, a report is the attempt, as before.
        await h.RequestAsync("scheduled");
        Assert.Equal(HttpStatusCode.NoContent, await ReportAsync(h, "scheduled"));
        var answered = await StatusAsync(h);
        Assert.Equal(h.App.Clock.UtcNow, answered["lastAttemptAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal("export-failed", answered["lastFailure"]!["reason"]!.GetValue<string>());
        Assert.False(answered["requestPending"]!.GetValue<bool>());

        // A failure after a host-requested create (the guest abandons, then reports) is recorded once, without moving the attempt.
        h.App.Clock.Advance(TimeSpan.FromHours(7));
        var id = await h.OpenIdAsync("scheduled");
        var attempt = h.App.Clock.UtcNow;
        Assert.Equal(HttpStatusCode.NoContent, await h.AbandonAsync(id));
        h.App.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.NoContent, await ReportAsync(h, "scheduled", "upload-failed"));
        var recorded = await StatusAsync(h);
        Assert.Equal(attempt, recorded["lastAttemptAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal("upload-failed", recorded["lastFailure"]!["reason"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, await ReportAsync(h, "scheduled", "verify-failed"));
        Assert.Equal("upload-failed", (await StatusAsync(h))["lastFailure"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_heartbeat_opens_the_request_once_and_a_busy_vm_gets_none()
    {
        using var h = await BackupHarness.CreateAsync();
        Assert.Empty(await h.HeartbeatAsync(busy: true));
        Assert.Null((await h.Store.GetVmAsync("alice", "dev", default))?.RequestPendingAt);
        var opened = h.App.Clock.UtcNow;
        Assert.Equal("scheduled", (await h.HeartbeatAsync())["backupTrigger"]!.GetValue<string>());
        h.App.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal("scheduled", (await h.HeartbeatAsync(busy: true))["backupTrigger"]!.GetValue<string>());
        var state = (await h.Store.GetVmAsync("alice", "dev", default))!;
        Assert.Equal(opened, state.RequestPendingAt);
        Assert.Equal(BackupTriggers.Scheduled, state.RequestTrigger);
        // Unanswered, it expires after an hour and the next heartbeat opens a new one.
        h.App.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal("scheduled", (await h.HeartbeatAsync())["backupTrigger"]!.GetValue<string>());
        Assert.Equal(h.App.Clock.UtcNow, (await h.Store.GetVmAsync("alice", "dev", default))!.RequestPendingAt);
    }

    [Fact]
    public async Task The_file_count_the_guest_sends_is_listed()
    {
        using var h = await BackupHarness.CreateAsync();
        var (status, opened) = await h.OpenAsync(fileCount: 1234);
        Assert.Equal(HttpStatusCode.Created, status);
        var id = opened["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, await h.PutKeysAsync(id));
        Assert.Equal(HttpStatusCode.OK, (await h.UploadAsync(id, [1])).Status);
        Assert.Equal(1234, (await h.ListAsync()).Single()!["fileCount"]!.GetValue<long>());
        using var invalid = await h.Guest.PostAsJsonAsync("/api/v1/vms/dev/backups", new { trigger = "manual", estimatedBytes = 1, fileCount = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
