using System.Text.Json.Nodes;
using Constructd.Store.Client;
using Constructd.Store.Records;
using Constructd.Sync.Profiles;
using Constructd.Sync.Replica;
using Constructd.Sync.Sealing;
using static Constructd.Tests.Store.StoreWrites;

namespace Constructd.Tests.Store;

/// <summary>
/// Two engines and one stand-alone store over HTTP (C.4), the conflict protocol (C.5) and the
/// payload-version rules (9.8).
/// </summary>
public sealed class EngineReplicaTests : IAsyncDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly StandaloneStoreFixture _store;
    private readonly FileRecordKeyProvider _keys;
    private readonly List<EngineReplica> _replicas = [];

    public EngineReplicaTests()
    {
        _store = new StandaloneStoreFixture(_directory.Path,
            ("engine-1", ParticipantKind.Engine, "e1"),
            ("engine-2", ParticipantKind.Engine, "e2"),
            ("engine-3", ParticipantKind.Engine, "e3"));
        _keys = new FileRecordKeyProvider(_directory.File("keys"));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var replica in _replicas) replica.Dispose();
        await _store.DisposeAsync();
        _directory.Dispose();
    }

    private (EngineReplica Replica, PartitionableClient Link) Engine(string id, int supported = RecordPayload.CurrentVersion)
    {
        var link = new PartitionableClient(_store.Client("engine-" + id[1..]));
        var replica = new EngineReplica(_directory.File($"replica-{id}-{supported}.db"), StandaloneStoreFixture.Account, id, link, _keys, supported);
        _replicas.Add(replica);
        return (replica, link);
    }

    private static JsonObject Profile(string fields) => Json("{\"name\":\"web\"," + fields + "}");

    private static string Commands(JsonNode? profile) => profile?["provisionCommands"]?.ToJsonString() ?? "null";

    private async Task<JsonObject> StoredProfileAsync(string name = "web")
    {
        var record = await _store.Store.GetAsync(StandaloneStoreFixture.Account, EngineReplica.ProfileId(name), default);
        return (JsonObject)RecordPayload.ValueOf(await OpenAsync(_keys, record!))!;
    }

    // ---- C.4 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Edits_made_during_a_partition_reach_the_store_after_it_ends()
    {
        var (one, linkOne) = Engine("e1");
        var (two, _) = Engine("e2");
        await one.WriteProfileAsync(Profile("""
            "provisionCommands":["npm install"],"hostPackages":["git"]
            """), "vm:a");
        await one.SyncAsync();
        await two.SyncAsync();
        Assert.Equal("""["npm install"]""", Commands(await two.ReadAsync(EngineReplica.ProfileId("web"))));

        // Engine one is cut off; both keep editing the same profile.
        linkOne.Partitioned = true;
        await one.WriteProfileAsync(Profile("""
            "provisionCommands":["npm install","npm run build"],"hostPackages":["git"]
            """), "vm:a");
        await one.WriteProfileAsync(Profile("""
            "provisionCommands":["npm install","npm run build"],"hostPackages":["git","jq"]
            """), "vm:a");
        await Assert.ThrowsAsync<StoreUnavailableException>(() => one.SyncAsync());
        Assert.Equal(2, await one.PendingAsync());
        // The local view shows the acknowledged edits.
        Assert.Equal("""["git","jq"]""", (await one.ReadAsync(EngineReplica.ProfileId("web")))!["hostPackages"]!.ToJsonString());

        await two.WriteProfileAsync(Profile("""
            "provisionCommands":["npm install","npm test"],"hostPackages":["git"]
            """), "vm:b");
        await two.SyncAsync();

        // The partition ends: the outbox merges against engine two's revision and goes through.
        linkOne.Partitioned = false;
        var pushed = await one.SyncAsync();
        Assert.Empty(pushed.ConflictIds);
        Assert.True(pushed.Merged >= 1);
        Assert.Equal(0, await one.PendingAsync());

        var stored = await StoredProfileAsync();
        Assert.Equal("""["npm install","npm run build","npm test"]""", Commands(stored));
        Assert.Equal("""["git","jq"]""", stored["hostPackages"]!.ToJsonString());

        // Both replicas converge on the store's state, and the cursor followed the feed.
        await two.SyncAsync();
        Assert.Equal(stored.ToJsonString(), (await one.ReadAsync(EngineReplica.ProfileId("web")))!.ToJsonString());
        Assert.Equal(stored.ToJsonString(), (await two.ReadAsync(EngineReplica.ProfileId("web")))!.ToJsonString());
        var head = (await _store.Store.FeedAsync(StandaloneStoreFixture.Account, 0, TimeSpan.Zero, 1000, default)).Cursor;
        Assert.Equal(head, await one.CursorAsync());
        Assert.Equal(head, await two.CursorAsync());
    }

    [Fact]
    public async Task A_merging_engine_keeps_the_origins_of_both_sides()
    {
        var (one, linkOne) = Engine("e1");
        var (two, _) = Engine("e2");
        await one.WriteProfileAsync(Profile("""
            "provisionCommands":["a"]
            """), "vm:a");
        await one.SyncAsync();
        await two.SyncAsync();

        linkOne.Partitioned = true;
        await one.WriteProfileAsync(Profile("""
            "provisionCommands":["a","one"]
            """), "vm:a");
        await two.WriteProfileAsync(Profile("""
            "provisionCommands":["a","two"]
            """), "vm:b");
        await two.SyncAsync();
        linkOne.Partitioned = false;
        await one.SyncAsync();

        var record = await _store.Store.GetAsync(StandaloneStoreFixture.Account, EngineReplica.ProfileId("web"), default);
        var payload = await OpenAsync(_keys, record!);
        Assert.Equal(["vm:a", "vm:b"], RecordPayload.OriginsOf(payload).Order());
        Assert.Equal("engine:e1", record!.Actor);
    }

    [Fact]
    public async Task A_lost_answer_is_resent_with_the_same_write_and_not_applied_twice()
    {
        var (one, link) = Engine("e1");
        await one.WriteProfileAsync(Profile("""
            "provisionCommands":["a"]
            """), null);
        link.LoseNextAnswer = true;
        await Assert.ThrowsAsync<StoreUnavailableException>(() => one.PushAsync());
        Assert.Equal(1, await one.PendingAsync());

        var pushed = await one.PushAsync();
        Assert.Equal(1, pushed.Sent);
        var history = await _store.Store.HistoryAsync(StandaloneStoreFixture.Account, EngineReplica.ProfileId("web"), 10, default);
        Assert.Single(history);
    }

    [Fact]
    public async Task A_second_queued_edit_is_merged_onto_what_the_first_became()
    {
        var (one, linkOne) = Engine("e1");
        var (two, _) = Engine("e2");
        await one.WriteProfileAsync(Profile("""
            "provisionCommands":["a"],"hostPackages":["x"]
            """), null);
        await one.SyncAsync();
        await two.SyncAsync();

        linkOne.Partitioned = true;
        await one.WriteProfileAsync(Profile("""
            "provisionCommands":["a","b"],"hostPackages":["x"]
            """), null);
        await one.WriteProfileAsync(Profile("""
            "provisionCommands":["a","b","c"],"hostPackages":["x"]
            """), null);
        await two.WriteProfileAsync(Profile("""
            "provisionCommands":["a"],"hostPackages":["x","y"]
            """), null);
        await two.SyncAsync();
        linkOne.Partitioned = false;
        await one.SyncAsync();

        // The first edit merged with engine two's package; the second must not overwrite that.
        var stored = await StoredProfileAsync();
        Assert.Equal("""["a","b","c"]""", Commands(stored));
        Assert.Equal("""["x","y"]""", stored["hostPackages"]!.ToJsonString());
    }

    [Fact]
    public async Task Payload_versions_newer_and_older_participants()
    {
        // A newer engine (payload version 2) writes; the older one (version 1) refuses to rewrite
        // that record, and keeps the edit queued as failed instead of losing it.
        var (newer, _) = Engine("e2", supported: 2);
        var (older, _) = Engine("e1");
        await newer.WriteProfileAsync(Profile("""
            "provisionCommands":["a"]
            """), null);
        await newer.SyncAsync();
        await older.SyncAsync();
        await Assert.ThrowsAsync<PayloadTooNewException>(() => older.WriteProfileAsync(Profile("""
            "provisionCommands":["b"]
            """), null));

        // The other way round: an older payload with a field the newer engine added later.
        var v1 = older;
        var record = await _store.Store.GetAsync(StandaloneStoreFixture.Account, EngineReplica.ProfileId("web"), default);
        var envelope = new RecordEnvelope(record!.Id, "profile", 1, "engine:e2", record.Revision, Guid.NewGuid().ToString("n"), false);
        var payload = Json("""{"payloadVersion":1,"value":{"name":"web","provisionCommands":["a"]},"origins":[],"addedLater":"keep me"}""");
        var key = await _keys.GetKeyAsync(StandaloneStoreFixture.Account, 1, default);
        Assert.IsType<WriteOutcome.Accepted>(await _store.Client("engine-2").WriteAsync(
            new RecordWrite(envelope.Id, envelope.Kind, 1, envelope.Actor, envelope.BaseRevision, envelope.WriteId, false, RecordSealer.Seal(key, envelope, payload)), default));
        await v1.SyncAsync();
        await v1.WriteProfileAsync(Profile("""
            "provisionCommands":["a","b"]
            """), null);
        await v1.SyncAsync();
        var rewritten = await OpenAsync(_keys, (await _store.Store.GetAsync(StandaloneStoreFixture.Account, EngineReplica.ProfileId("web"), default))!);
        Assert.Equal("keep me", rewritten["addedLater"]!.GetValue<string>());
        Assert.Equal("""["a","b"]""", Commands(RecordPayload.ValueOf(rewritten)));
    }

    // ---- deletion (9.5) ----------------------------------------------------------------------------

    [Fact]
    public async Task An_edit_of_a_profile_deleted_meanwhile_is_a_conflict_also_after_it_was_created_again()
    {
        var (one, linkOne) = Engine("e1");
        var (two, _) = Engine("e2");
        await one.WriteProfileAsync(Profile("""
            "provisionCommands":["a"]
            """), null);
        await one.SyncAsync();
        await two.SyncAsync();

        linkOne.Partitioned = true;
        await one.WriteProfileAsync(Profile("""
            "provisionCommands":["a","edited"]
            """), "vm:a");
        await two.DeleteProfileAsync("web", null);
        await two.SyncAsync();
        await two.WriteProfileAsync(Profile("""
            "provisionCommands":["fresh"]
            """), null);
        await two.SyncAsync();

        linkOne.Partitioned = false;
        var pushed = await one.SyncAsync();
        var conflictId = Assert.Single(pushed.ConflictIds);
        Assert.Equal("""["fresh"]""", Commands(await StoredProfileAsync()));
        var conflict = Assert.Single(await one.ConflictsAsync());
        Assert.Equal(conflictId, conflict.Id);
        Assert.Equal(["deleted"], conflict.Fields);
        Assert.Equal("""["a","edited"]""", Commands(conflict.Value["ours"]));
    }

    [Fact]
    public async Task A_deletion_against_a_concurrent_edit_is_a_conflict()
    {
        var (one, linkOne) = Engine("e1");
        var (two, _) = Engine("e2");
        await one.WriteProfileAsync(Profile("""
            "provisionCommands":["a"]
            """), null);
        await one.SyncAsync();
        await two.SyncAsync();

        linkOne.Partitioned = true;
        await one.DeleteProfileAsync("web", null);
        await two.WriteProfileAsync(Profile("""
            "provisionCommands":["a","b"]
            """), null);
        await two.SyncAsync();
        linkOne.Partitioned = false;
        Assert.Single((await one.SyncAsync()).ConflictIds);
        Assert.Equal("""["a","b"]""", Commands(await StoredProfileAsync()));
    }

    [Fact]
    public async Task A_deletion_based_on_a_revision_before_a_deletion_and_recreation_is_a_conflict()
    {
        var (one, linkOne) = Engine("e1");
        var (two, _) = Engine("e2");
        await one.WriteProfileAsync(Profile("\"provisionCommands\":[\"a\"]"), null);
        await one.SyncAsync();
        await two.SyncAsync();

        linkOne.Partitioned = true;
        await one.DeleteProfileAsync("web", null);
        await two.DeleteProfileAsync("web", null);
        await two.SyncAsync();
        // Created again with the very same content.
        await two.WriteProfileAsync(Profile("\"provisionCommands\":[\"a\"]"), null);
        await two.SyncAsync();

        linkOne.Partitioned = false;
        Assert.Single((await one.SyncAsync()).ConflictIds);
        Assert.False((await _store.Store.GetAsync(StandaloneStoreFixture.Account, EngineReplica.ProfileId("web"), default))!.Deleted);
    }

    [Fact]
    public async Task A_refused_edit_is_kept_and_goes_through_once_the_engine_understands_the_payload()
    {
        var file = _directory.File("replica-upgrade.db");
        var link = new PartitionableClient(_store.Client("engine-1"));
        var v1 = new EngineReplica(file, StandaloneStoreFixture.Account, "e1", link, _keys, supportedPayloadVersion: 1);
        var (newer, _) = Engine("e2", supported: 2);
        await v1.WriteProfileAsync(Profile("\"provisionCommands\":[\"a\"]"), null);
        await v1.SyncAsync();
        await newer.SyncAsync();

        link.Partitioned = true;
        await v1.WriteProfileAsync(Profile("\"provisionCommands\":[\"a\",\"one\"]"), null);
        await v1.WriteProfileAsync(Profile("\"provisionCommands\":[\"a\",\"one\",\"two\"]"), null);
        await newer.WriteProfileAsync(Profile("\"provisionCommands\":[\"a\"],\"hostPackages\":[\"x\"]"), null);
        await newer.SyncAsync();
        link.Partitioned = false;

        // Every push reports the refusal again; nothing is dropped.
        Assert.Single((await v1.PushAsync()).Failed);
        Assert.Single((await v1.PushAsync()).Failed);
        Assert.Equal(2, await v1.PendingAsync());
        Assert.Equal("""["a","one","two"]""", Commands(await v1.ReadAsync(EngineReplica.ProfileId("web"))));
        v1.Dispose();

        // The engine is updated: the same file, a build that understands version 2.
        var v2 = new EngineReplica(file, StandaloneStoreFixture.Account, "e1", link, _keys, supportedPayloadVersion: 2);
        _replicas.Add(v2);
        var pushed = await v2.SyncAsync();
        Assert.Empty(pushed.Failed);
        Assert.Equal(0, await v2.PendingAsync());
        var stored = await StoredProfileAsync();
        Assert.Equal("""["a","one","two"]""", Commands(stored));
        Assert.Equal("""["x"]""", stored["hostPackages"]!.ToJsonString());
    }

    // ---- C.5 ---------------------------------------------------------------------------------------

    /// <summary>Engine one and two edit the same command differently while one is partitioned.</summary>
    private async Task<(EngineReplica One, EngineReplica Two, ConflictView Conflict)> ConflictAsync()
    {
        var (one, linkOne) = Engine("e1");
        var (two, _) = Engine("e2");
        await one.WriteProfileAsync(Profile("""
            "provisionCommands":["npm install"],"hostPackages":["git"]
            """), null);
        await one.SyncAsync();
        await two.SyncAsync();

        linkOne.Partitioned = true;
        await one.WriteProfileAsync(Profile("""
            "provisionCommands":["npm ci"],"hostPackages":["git","jq"]
            """), "vm:a");
        await two.WriteProfileAsync(Profile("""
            "provisionCommands":["pnpm install"],"hostPackages":["git"]
            """), "vm:b");
        await two.SyncAsync();
        linkOne.Partitioned = false;
        var pushed = await one.SyncAsync();
        Assert.Single(pushed.ConflictIds);
        await two.SyncAsync();
        return (one, two, Assert.Single(await two.ConflictsAsync()));
    }

    [Fact]
    public async Task An_unmergeable_edit_becomes_one_conflict_record_carrying_the_merged_value_of_every_other_field()
    {
        var (_, _, conflict) = await ConflictAsync();
        Assert.Equal(EngineReplica.ProfileId("web"), conflict.ProfileId);
        Assert.Equal(["provisionCommands"], conflict.Fields);
        Assert.Equal("""["npm install"]""", Commands(conflict.Value["base"]));
        Assert.Equal("""["npm ci"]""", Commands(conflict.Value["ours"]));
        Assert.Equal("""["pnpm install"]""", Commands(conflict.Value["theirs"]));
        // The merged value: jq from ours is in; the conflicted field carries theirs.
        Assert.Equal("""["git","jq"]""", conflict.Value["merged"]!["hostPackages"]!.ToJsonString());
        Assert.Equal("vm:a", conflict.Value["identities"]!["ours"]!["origins"]![0]!.GetValue<string>());
        Assert.Equal("engine:e2", conflict.Value["identities"]!["theirs"]!["actor"]!.GetValue<string>());
        // The profile itself is untouched.
        Assert.Equal("""["pnpm install"]""", Commands(await StoredProfileAsync()));
    }

    [Fact]
    public void Identical_conflict_inputs_produce_one_conflict_id()
    {
        var b = Profile("\"provisionCommands\":[\"a\"]");
        var o = Profile("\"provisionCommands\":[\"b\"]");
        var t = Profile("\"provisionCommands\":[\"c\"]");
        var id = EngineReplica.ConflictId("profile:web", b, o, t);
        Assert.Equal(id, EngineReplica.ConflictId("profile:web", b.DeepClone(), o.DeepClone(), t.DeepClone()));
        // Key order does not matter, content does.
        Assert.Equal(id, EngineReplica.ConflictId("profile:web", b, Json("""{"provisionCommands":["b"],"name":"web"}"""), t));
        Assert.NotEqual(id, EngineReplica.ConflictId("profile:web", b, t, o));
    }

    [Fact]
    public async Task The_same_conflict_computed_by_two_engines_is_written_once()
    {
        var (one, linkOne) = Engine("e1");
        var (two, linkTwo) = Engine("e2");
        var (three, _) = Engine("e3");
        await one.WriteProfileAsync(Profile("\"provisionCommands\":[\"a\"]"), null);
        await one.SyncAsync();
        await two.SyncAsync();
        await three.SyncAsync();

        // One and two make the same edit while three makes a conflicting one.
        linkOne.Partitioned = linkTwo.Partitioned = true;
        await one.WriteProfileAsync(Profile("\"provisionCommands\":[\"b\"]"), null);
        await two.WriteProfileAsync(Profile("\"provisionCommands\":[\"b\"]"), null);
        await three.WriteProfileAsync(Profile("\"provisionCommands\":[\"c\"]"), null);
        await three.SyncAsync();
        linkOne.Partitioned = linkTwo.Partitioned = false;
        var first = Assert.Single((await one.SyncAsync()).ConflictIds);
        var second = Assert.Single((await two.SyncAsync()).ConflictIds);
        Assert.Equal(first, second);
        var page = await _store.Store.FeedAsync(StandaloneStoreFixture.Account, 0, TimeSpan.Zero, 100, default);
        Assert.Single(page.Records, r => r.Kind == "conflict");
    }

    [Fact]
    public async Task A_resolve_writes_the_profile_and_closes_the_conflict_in_one_batch()
    {
        var (one, two, conflict) = await ConflictAsync();
        var resolved = (JsonObject)conflict.Value["merged"]!.DeepClone();
        resolved["provisionCommands"] = new JsonArray("pnpm install --frozen-lockfile");

        var outcome = Assert.IsType<ResolveOutcome.Resolved>(await two.ResolveAsync(conflict.Id, resolved, "vm:b"));
        var stored = await StoredProfileAsync();
        Assert.Equal("""["pnpm install --frozen-lockfile"]""", Commands(stored));
        Assert.Equal("""["git","jq"]""", stored["hostPackages"]!.ToJsonString());
        Assert.True((await _store.Store.GetAsync(StandaloneStoreFixture.Account, conflict.Id, default))!.Deleted);
        Assert.Empty(await two.ConflictsAsync());
        await one.SyncAsync();
        Assert.Empty(await one.ConflictsAsync());
        Assert.Equal(outcome.ProfileRevision, (await one.GetAsync(EngineReplica.ProfileId("web")))!.Revision);
    }

    [Fact]
    public async Task Two_resolvers_at_once_leave_one_result()
    {
        var (one, two, conflict) = await ConflictAsync();
        await one.SyncAsync();
        var a = (JsonObject)conflict.Value["merged"]!.DeepClone();
        a["provisionCommands"] = new JsonArray("from one");
        var b = (JsonObject)conflict.Value["merged"]!.DeepClone();
        b["provisionCommands"] = new JsonArray("from two");

        var outcomes = await Task.WhenAll(one.ResolveAsync(conflict.Id, a, null), two.ResolveAsync(conflict.Id, b, null));
        Assert.Single(outcomes, o => o is ResolveOutcome.Resolved);
        Assert.Single(outcomes, o => o is ResolveOutcome.AlreadyClosed);
        var winner = outcomes[0] is ResolveOutcome.Resolved ? "from one" : "from two";
        Assert.Equal($"[\"{winner}\"]", Commands(await StoredProfileAsync()));
    }

    [Fact]
    public async Task A_stale_resolve_fails_atomically_and_the_conflict_is_computed_again()
    {
        var (one, two, conflict) = await ConflictAsync();

        // The profile changes after the conflict was computed, in the same command.
        await one.SyncAsync();
        await one.WriteProfileAsync(Profile("""
            "provisionCommands":["yarn install"],"hostPackages":["git"]
            """), null);
        await one.SyncAsync();
        var before = await StoredProfileAsync();

        var resolved = (JsonObject)conflict.Value["merged"]!.DeepClone();
        resolved["provisionCommands"] = new JsonArray("npm ci");
        var outcome = Assert.IsType<ResolveOutcome.Recomputed>(await two.ResolveAsync(conflict.Id, resolved, null));

        // The profile is unchanged, the old conflict closed and a new one open against the new state.
        Assert.Equal(before.ToJsonString(), (await StoredProfileAsync()).ToJsonString());
        Assert.True((await _store.Store.GetAsync(StandaloneStoreFixture.Account, conflict.Id, default))!.Deleted);
        await two.SyncAsync();
        var again = Assert.Single(await two.ConflictsAsync());
        Assert.Equal(outcome.ConflictId, again.Id);
        Assert.Equal("""["yarn install"]""", Commands(again.Value["theirs"]));
        Assert.Equal("""["npm ci"]""", Commands(again.Value["ours"]));
    }

    [Fact]
    public async Task A_resolve_whose_profile_changed_elsewhere_carries_the_resolution_over()
    {
        var (one, two, conflict) = await ConflictAsync();
        await one.SyncAsync();
        // A change to another field after the conflict was computed.
        var changed = (JsonObject)(await one.ReadAsync(EngineReplica.ProfileId("web")))!.DeepClone();
        changed["sdks"] = new JsonObject { ["node"] = "22" };
        await one.WriteProfileAsync(changed, null);
        await one.SyncAsync();

        var resolved = (JsonObject)conflict.Value["merged"]!.DeepClone();
        resolved["provisionCommands"] = new JsonArray("npm ci");
        Assert.IsType<ResolveOutcome.Resolved>(await two.ResolveAsync(conflict.Id, resolved, null));
        var stored = await StoredProfileAsync();
        Assert.Equal("""["npm ci"]""", Commands(stored));
        Assert.Equal("""{"node":"22"}""", stored["sdks"]!.ToJsonString());
    }

    [Fact]
    public async Task A_resolve_carried_over_a_newer_profile_keeps_its_origins()
    {
        var (one, two, conflict) = await ConflictAsync();
        await one.SyncAsync();
        var changed = (JsonObject)(await one.ReadAsync(EngineReplica.ProfileId("web")))!.DeepClone();
        changed["sdks"] = new JsonObject { ["node"] = "22" };
        await one.WriteProfileAsync(changed, "vm:c");
        await one.SyncAsync();

        var resolved = (JsonObject)conflict.Value["merged"]!.DeepClone();
        resolved["provisionCommands"] = new JsonArray("npm ci");
        Assert.IsType<ResolveOutcome.Resolved>(await two.ResolveAsync(conflict.Id, resolved, "vm:b"));
        var record = await _store.Store.GetAsync(StandaloneStoreFixture.Account, EngineReplica.ProfileId("web"), default);
        Assert.Equal(["vm:a", "vm:b", "vm:c"], RecordPayload.OriginsOf(await OpenAsync(_keys, record!)).Order());
    }

    [Fact]
    public async Task An_invalid_profile_is_refused_before_it_is_queued()
    {
        var (one, _) = Engine("e1");
        var refused = await Assert.ThrowsAsync<InvalidProfileException>(() => one.WriteProfileAsync(Json("""{"name":"web","repos":"nope"}"""), null));
        Assert.Contains(refused.Problems, p => p.Field == "repos");
        Assert.Equal(0, await one.PendingAsync());
    }
}
