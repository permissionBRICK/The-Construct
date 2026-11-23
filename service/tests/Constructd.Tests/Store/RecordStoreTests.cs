using Constructd.Store.Records;
using static Constructd.Tests.Store.StoreWrites;

namespace Constructd.Tests.Store;

/// <summary>The store's compare-and-swap, batch, tombstone, feed and history behaviour (9.2, 9.5).</summary>
public sealed class RecordStoreTests : IDisposable
{
    private static readonly StorePrincipal Engine = StorePrincipal.Engine("alice", "e1");
    private static readonly StorePrincipal Device = StorePrincipal.Device("alice", "laptop");
    private readonly ManualTime _time = new();
    private readonly RecordStore _store;

    public RecordStoreTests() => _store = new RecordStore(":memory:", _time);

    public void Dispose() => _store.Dispose();

    private async Task<StoredRecord> AcceptedAsync(StorePrincipal by, RecordWrite write) =>
        Assert.IsType<WriteOutcome.Accepted>(await _store.WriteAsync(by, write, default)).Record;

    [Fact]
    public async Task Compare_and_swap_assigns_revisions_and_412_returns_the_current_record()
    {
        var first = await AcceptedAsync(Engine, Write(Engine, "profile:a", "profile", 0));
        var second = await AcceptedAsync(Engine, Write(Engine, "profile:a", "profile", first.Revision));
        Assert.True(second.Revision > first.Revision);

        var stale = Assert.IsType<WriteOutcome.Stale>(await _store.WriteAsync(Engine, Write(Engine, "profile:a", "profile", first.Revision), default));
        Assert.Equal(second.Revision, stale.Conflict.Current!.Revision);
        Assert.Equal(second.WriteId, stale.Conflict.Current.WriteId);

        // Creating a name that exists is a stale write too.
        Assert.IsType<WriteOutcome.Stale>(await _store.WriteAsync(Engine, Write(Engine, "profile:a", "profile", 0), default));
        // And a write based on a record that does not exist answers with no current record.
        var missing = Assert.IsType<WriteOutcome.Stale>(await _store.WriteAsync(Engine, Write(Engine, "profile:b", "profile", 5), default));
        Assert.Null(missing.Conflict.Current);
    }

    [Fact]
    public async Task The_actor_must_be_the_caller_and_the_kind_table_holds()
    {
        var other = StorePrincipal.Engine("alice", "e2");
        var claimed = Assert.IsType<WriteOutcome.Refused>(await _store.WriteAsync(Engine, Write(other, "profile:a", "profile", 0), default));
        Assert.Equal((403, "actor-mismatch"), (claimed.Status, claimed.Code));

        // 9.1: engines write profile, vm-applied, secret, conflict, upstream-applied; devices write
        // setting, vm-desired, engine, device, secret, upstream, review.
        string[] engineKinds = ["profile", "vm-applied", "secret", "conflict", "upstream-applied"];
        string[] deviceKinds = ["setting", "vm-desired", "engine", "device", "secret", "upstream", "review"];
        foreach (var kind in engineKinds.Union(deviceKinds))
        {
            var byEngine = await _store.WriteAsync(Engine, Write(Engine, "e-" + kind, kind, 0), default);
            var byDevice = await _store.WriteAsync(Device, Write(Device, "d-" + kind, kind, 0), default);
            Assert.Equal(engineKinds.Contains(kind), byEngine is WriteOutcome.Accepted);
            Assert.Equal(deviceKinds.Contains(kind), byDevice is WriteOutcome.Accepted);
            if (byDevice is WriteOutcome.Refused refused) Assert.Equal((403, "kind-not-writable"), (refused.Status, refused.Code));
        }

        var unknown = Assert.IsType<WriteOutcome.Refused>(await _store.WriteAsync(Engine, Write(Engine, "x", "invented", 0), default));
        Assert.Equal("unknown-kind", unknown.Code);
    }

    [Fact]
    public async Task A_write_outside_the_active_epoch_is_refused()
    {
        Assert.IsType<WriteOutcome.Accepted>(await _store.WriteAsync(Engine, Write(Engine, "profile:a", "profile", 0, epoch: 1), default));
        await _store.SetActiveEpochAsync("alice", 2, default);
        var refused = Assert.IsType<WriteOutcome.Refused>(await _store.WriteAsync(Engine, Write(Engine, "profile:b", "profile", 0, epoch: 1), default));
        Assert.Equal("epoch-not-active", refused.Code);
        Assert.IsType<WriteOutcome.Accepted>(await _store.WriteAsync(Engine, Write(Engine, "profile:b", "profile", 0, epoch: 2), default));

        // Epochs are per account.
        var bob = StorePrincipal.Engine("bob", "e1");
        Assert.IsType<WriteOutcome.Accepted>(await _store.WriteAsync(bob, Write(bob, "profile:b", "profile", 0, epoch: 1), default));
    }

    [Fact]
    public async Task A_batch_applies_every_write_or_none()
    {
        var a = await AcceptedAsync(Engine, Write(Engine, "profile:a", "profile", 0));
        var c = await AcceptedAsync(Engine, Write(Engine, "conflict:1", "conflict", 0));

        var stale = Assert.IsType<BatchOutcome.Stale>(await _store.BatchAsync(Engine,
            [Write(Engine, "profile:a", "profile", a.Revision), Write(Engine, "conflict:1", "conflict", c.Revision - 1, deleted: true)], default));
        Assert.Equal("conflict:1", Assert.Single(stale.Conflict.Conflicts).Id);
        Assert.Equal(a.Revision, (await _store.GetAsync("alice", "profile:a", default))!.Revision);

        var accepted = Assert.IsType<BatchOutcome.Accepted>(await _store.BatchAsync(Engine,
            [Write(Engine, "profile:a", "profile", a.Revision), Write(Engine, "conflict:1", "conflict", c.Revision, deleted: true)], default));
        Assert.Equal(2, accepted.Records.Count);
        Assert.True((await _store.GetAsync("alice", "conflict:1", default))!.Deleted);

        // A refusal anywhere refuses the whole batch.
        var refused = await _store.BatchAsync(Engine,
            [Write(Engine, "profile:z", "profile", 0), Write(Engine, "setting:z", "setting", 0)], default);
        Assert.IsType<BatchOutcome.Refused>(refused);
        Assert.Null(await _store.GetAsync("alice", "profile:z", default));
    }

    [Fact]
    public async Task A_tombstone_stays_and_an_edit_from_before_the_deletion_stays_detectable_after_recreation()
    {
        var created = await AcceptedAsync(Engine, Write(Engine, "profile:a", "profile", 0));
        var deleted = await AcceptedAsync(Engine, Write(Engine, "profile:a", "profile", created.Revision, deleted: true));
        Assert.True(deleted.Deleted);
        Assert.Equal(deleted.Revision, deleted.DeletedRevision);

        // Re-creating the name writes a new revision of the same record, based on the tombstone.
        Assert.IsType<WriteOutcome.Stale>(await _store.WriteAsync(Engine, Write(Engine, "profile:a", "profile", 0), default));
        var recreated = await AcceptedAsync(Engine, Write(Engine, "profile:a", "profile", deleted.Revision));
        Assert.False(recreated.Deleted);
        Assert.Equal(deleted.Revision, recreated.DeletedRevision);

        // An edit based on the revision before the deletion: refused, and the answer names the
        // deletion so the merger turns it into a conflict (9.5) ...
        var stale = Assert.IsType<WriteOutcome.Stale>(await _store.WriteAsync(Engine, Write(Engine, "profile:a", "profile", created.Revision), default));
        Assert.True(stale.Conflict.Current!.DeletedRevision > created.Revision);

        // ... and that stays so beyond the history limit.
        var revision = recreated.Revision;
        for (var i = 0; i < RecordStore.HistoryVersions + 5; i++)
            revision = (await AcceptedAsync(Engine, Write(Engine, "profile:a", "profile", revision))).Revision;
        _time.Now += TimeSpan.FromDays(200);
        revision = (await AcceptedAsync(Engine, Write(Engine, "profile:a", "profile", revision))).Revision;
        Assert.Equal(deleted.Revision, (await _store.GetAsync("alice", "profile:a", default))!.DeletedRevision);

        // Deleting a record that never existed is not a tombstone.
        var missing = Assert.IsType<WriteOutcome.Refused>(await _store.WriteAsync(Engine, Write(Engine, "profile:none", "profile", 0, deleted: true), default));
        Assert.Equal(404, missing.Status);
    }

    [Fact]
    public async Task History_keeps_twenty_versions_or_ninety_days_whichever_is_more()
    {
        var revision = 0L;
        for (var i = 0; i < 30; i++)
            revision = (await AcceptedAsync(Engine, Write(Engine, "profile:a", "profile", revision))).Revision;
        // All 30 are younger than 90 days.
        Assert.Equal(30, (await _store.HistoryAsync("alice", "profile:a", 100, default)).Count);

        _time.Now += TimeSpan.FromDays(91);
        revision = (await AcceptedAsync(Engine, Write(Engine, "profile:a", "profile", revision))).Revision;
        var history = await _store.HistoryAsync("alice", "profile:a", 100, default);
        Assert.Equal(RecordStore.HistoryVersions, history.Count);
        Assert.Equal(revision, history[0].Revision);
    }

    [Fact]
    public async Task A_repeated_write_id_answers_with_the_version_it_produced()
    {
        var write = Write(Engine, "profile:a", "profile", 0);
        var first = await AcceptedAsync(Engine, write);
        var later = await AcceptedAsync(Engine, Write(Engine, "profile:a", "profile", first.Revision));

        // The engine lost the answer and sends again: same version, nothing written twice.
        var again = await AcceptedAsync(Engine, write);
        Assert.Equal(first.Revision, again.Revision);
        Assert.Equal(later.Revision, (await _store.GetAsync("alice", "profile:a", default))!.Revision);

        var reused = Assert.IsType<WriteOutcome.Refused>(await _store.WriteAsync(Engine, write with { BaseRevision = later.Revision }, default));
        Assert.Equal("write-id-reused", reused.Code);
    }

    [Fact]
    public async Task The_feed_returns_changes_after_the_cursor_and_long_polls()
    {
        var a = await AcceptedAsync(Engine, Write(Engine, "profile:a", "profile", 0));
        var b = await AcceptedAsync(Engine, Write(Engine, "profile:b", "profile", 0));
        var page = await _store.FeedAsync("alice", 0, TimeSpan.Zero, 100, default);
        Assert.Equal(["profile:a", "profile:b"], page.Records.Select(r => r.Id));
        Assert.Equal(b.Revision, page.Cursor);

        var a2 = await AcceptedAsync(Engine, Write(Engine, "profile:a", "profile", a.Revision));
        page = await _store.FeedAsync("alice", page.Cursor, TimeSpan.Zero, 100, default);
        Assert.Equal(a2.Revision, Assert.Single(page.Records).Revision);

        // Another account's records are not in this feed.
        var bob = StorePrincipal.Engine("bob", "e1");
        await _store.WriteAsync(bob, Write(bob, "profile:x", "profile", 0), default);
        Assert.Empty((await _store.FeedAsync("alice", page.Cursor, TimeSpan.Zero, 100, default)).Records);

        // Long-poll: an empty feed waits for the next accepted write. The wait runs on the real
        // clock here, so the waiting side uses the system time provider.
        using var live = new RecordStore(":memory:");
        var waiting = live.FeedAsync("alice", 0, TimeSpan.FromSeconds(20), 100, default);
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        await live.WriteAsync(Engine, Write(Engine, "profile:a", "profile", 0), default);
        var woken = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(woken.Records);
    }

    [Fact]
    public async Task Export_and_import_carry_the_whole_account_with_its_revisions()
    {
        var a = await AcceptedAsync(Engine, Write(Engine, "profile:a", "profile", 0));
        var a2 = await AcceptedAsync(Engine, Write(Engine, "profile:a", "profile", a.Revision, deleted: true));
        var s = await AcceptedAsync(Device, Write(Device, "setting:x", "setting", 0));
        await _store.SetActiveEpochAsync("alice", 3, default);
        var export = await _store.ExportAsync("alice", default);

        using var target = new RecordStore(":memory:", _time);
        Assert.True(await target.ImportAsync("alice", export, default));
        Assert.False(await target.ImportAsync("alice", export, default));
        Assert.Equal(3, await target.ActiveEpochAsync("alice", default));
        var moved = (await target.GetAsync("alice", "profile:a", default))!;
        Assert.Equal((a2.Revision, true, a2.Revision), (moved.Revision, moved.Deleted, moved.DeletedRevision));
        Assert.Equal(s.Ciphertext, (await target.GetAsync("alice", "setting:x", default))!.Ciphertext);
        Assert.Equal(2, (await target.HistoryAsync("alice", "profile:a", 10, default)).Count);

        // New revisions continue after the imported ones.
        var next = Assert.IsType<WriteOutcome.Accepted>(await target.WriteAsync(Device, Write(Device, "setting:x", "setting", s.Revision, epoch: 3), default));
        Assert.True(next.Record.Revision > a2.Revision);
    }
}
