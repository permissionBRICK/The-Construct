using System.Net;
using System.Net.Http.Json;
using Constructd.Api.Backups;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Tests.Support;

namespace Constructd.Tests.Backups;

/// <summary>The hourly backup cleanup (docs/plans/host-config-backups.md §3.6 "Cleanup service").</summary>
public sealed class BackupCleanupTests
{
    private static Task<BackupCleanupResult?> RunAsync(BackupHarness h) => h.App.Service<BackupCleanupService>().RunAsync(default);

    private static string Dir(BackupHarness h, string owner = "alice", string vm = "dev") =>
        Path.Combine(h.Root, BackupFileStore.OwnerSegment(owner), vm);

    [Fact]
    public async Task Abandoned_sessions_and_orphaned_files_are_removed_and_complete_backups_kept()
    {
        using var h = await BackupHarness.CreateAsync();
        var complete = await h.BackupAsync();
        var abandoned = await h.OpenIdAsync();
        Directory.CreateDirectory(Dir(h));
        await File.WriteAllBytesAsync(Path.Combine(Dir(h), abandoned + ".age.partial"), [1, 2]);
        var orphanPartial = Path.Combine(Dir(h), new string('c', 32) + ".age.partial");
        var orphanPayload = Path.Combine(Dir(h), new string('d', 32) + ".age");
        var unrelated = Path.Combine(Dir(h), "notes.txt");
        foreach (var path in new[] { orphanPartial, orphanPayload, unrelated }) await File.WriteAllBytesAsync(path, [3]);

        // A young open session stays; a partial file nothing is writing goes, whether or not its session exists.
        h.App.Clock.Advance(TimeSpan.FromHours(1));
        var young = await RunAsync(h);
        Assert.Equal(0, young!.StaleSessions);
        Assert.Equal(3, young.OrphanFiles);
        Assert.NotNull(await h.Store.GetAsync(abandoned, default));
        Assert.False(File.Exists(Path.Combine(Dir(h), abandoned + ".age.partial")));
        Assert.False(File.Exists(orphanPartial));
        Assert.False(File.Exists(orphanPayload));

        h.App.Clock.Advance(TimeSpan.FromHours(1));
        var result = await RunAsync(h);
        Assert.Equal(1, result!.StaleSessions);
        Assert.Null(await h.Store.GetAsync(abandoned, default));
        Assert.True(File.Exists(Path.Combine(Dir(h), complete + ".age")));
        Assert.True(File.Exists(unrelated));
        Assert.Equal(complete, (await h.ListAsync()).Single()!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_upload_in_flight_is_never_touched()
    {
        using var h = await BackupHarness.CreateAsync();
        var id = await h.OpenIdAsync();
        var upload = h.Coordinator.BeginUpload(id, default)!;
        Directory.CreateDirectory(Dir(h));
        await File.WriteAllBytesAsync(Path.Combine(Dir(h), id + ".age.partial"), [1]);
        h.App.Clock.Advance(TimeSpan.FromHours(5));
        Assert.Equal(0, (await RunAsync(h))!.OrphanFiles);
        Assert.NotNull(await h.Store.GetAsync(id, default));
        Assert.True(File.Exists(Path.Combine(Dir(h), id + ".age.partial")));
        // And it still blocks a second session.
        Assert.Equal(HttpStatusCode.Conflict, (await h.OpenAsync()).Status);
        h.Coordinator.EndUpload(id, upload);
    }

    [Fact]
    public async Task Backups_of_a_deleted_vm_are_kept_for_the_retention_and_then_deleted()
    {
        using var h = await BackupHarness.CreateAsync();
        var id = await h.BackupAsync();
        await BackupHarness.DeleteVmAsync(h.Alice, "dev");
        var gone = h.App.Clock.UtcNow;
        Assert.Equal(1, (await RunAsync(h))!.MarkedMissing);
        Assert.Equal(gone, (await h.Store.GetVmAsync("alice", "dev", default))!.VmMissingSince);

        h.App.Clock.Advance(TimeSpan.FromDays(14));
        var kept = await RunAsync(h);
        Assert.Equal(0, kept!.Purged);
        Assert.Equal(0, kept.MarkedMissing);
        Assert.NotNull(await h.Store.GetAsync(id, default));

        h.App.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, (await RunAsync(h))!.Purged);
        Assert.Null(await h.Store.GetAsync(id, default));
        Assert.Null(await h.Store.GetVmAsync("alice", "dev", default));
        Assert.False(Directory.Exists(Dir(h)));
    }

    [Fact]
    public async Task A_vm_of_the_same_owner_and_name_ends_the_missing_period_and_another_owners_does_not()
    {
        using var h = await BackupHarness.CreateAsync();
        var id = await h.BackupAsync();
        await BackupHarness.DeleteVmAsync(h.Alice, "dev");
        await RunAsync(h);

        // Another owner's VM of that name does not count as this pair's VM.
        using var carol = await h.App.CreateUserClientAsync("carol");
        await carol.CreateVmAsync("dev");
        await RunAsync(h);
        Assert.NotNull((await h.Store.GetVmAsync("alice", "dev", default))!.VmMissingSince);
        await BackupHarness.DeleteVmAsync(carol, "dev");

        await h.Alice.CreateVmAsync("dev");
        await RunAsync(h);
        Assert.Null((await h.Store.GetVmAsync("alice", "dev", default))!.VmMissingSince);
        h.App.Clock.Advance(TimeSpan.FromDays(30));
        await RunAsync(h);
        Assert.NotNull(await h.Store.GetAsync(id, default));
    }

    [Fact]
    public async Task The_hourly_pass_applies_a_lowered_keep_and_expires_requests()
    {
        using var h = await BackupHarness.CreateAsync();
        Assert.Equal(HttpStatusCode.OK, (await h.Alice.PutAsJsonAsync("/api/v1/vms/dev/backups/settings", new { keep = 10 })).StatusCode);
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await h.BackupAsync());
            h.App.Clock.Advance(TimeSpan.FromMinutes(10));
        }
        Assert.Equal(3, (await h.ListAsync()).Count);
        Assert.Equal(HttpStatusCode.OK, (await h.Alice.PutAsJsonAsync("/api/v1/vms/dev/backups/settings", new { keep = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await h.Alice.PostAsync("/api/v1/vms/dev/backups/request", null)).StatusCode);
        h.App.Clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(2, (await RunAsync(h))!.Rotated);
        Assert.Equal(ids[^1], (await h.ListAsync()).Single()!["id"]!.GetValue<string>());
        Assert.Single(h.Files("*.age"));
        Assert.Null((await h.Store.GetVmAsync("alice", "dev", default))!.RequestPendingAt);
    }

    [Fact]
    public async Task Cleanup_skips_a_pass_during_maintenance()
    {
        using var h = await BackupHarness.CreateAsync();
        var gate = h.App.Service<IMaintenanceGate>();
        gate.Enter(MaintenanceState.Draining, "update");
        Assert.Null(await RunAsync(h));
        gate.Reopen();
        Assert.NotNull(await RunAsync(h));
    }
}
