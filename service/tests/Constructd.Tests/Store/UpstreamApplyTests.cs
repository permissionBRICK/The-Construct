using System.Text.Json.Nodes;
using Constructd.Store.Client;
using Constructd.Store.Records;
using Constructd.Sync.Profiles;
using Constructd.Sync.Replica;
using Constructd.Sync.Sealing;
using static Constructd.Tests.Store.StoreWrites;

namespace Constructd.Tests.Store;

/// <summary>
/// An upstream config repo's new version of a tracked file merged into the profile by an engine (9.7): base = the
/// stored base, the local profile, the new upstream version; the profile, or the conflict, together with the new base
/// in one batch; a retry with the same operation or commit changes nothing (C.5).
/// </summary>
public sealed class UpstreamApplyTests : IAsyncLifetime
{
    private const string Url = "https://git.example.test/team/configs.git";
    private const string Path = "projects/web.json";

    private readonly TempDirectory _directory = new();
    private readonly StandaloneStoreFixture _store;
    private readonly FileRecordKeyProvider _keys;
    private readonly List<EngineReplica> _replicas = [];

    public UpstreamApplyTests()
    {
        _store = new StandaloneStoreFixture(_directory.Path, ("engine-1", ParticipantKind.Engine, "e1"), ("engine-2", ParticipantKind.Engine, "e2"));
        _keys = new FileRecordKeyProvider(_directory.File("keys"));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var replica in _replicas) replica.Dispose();
        await _store.DisposeAsync();
        _directory.Dispose();
    }

    private (EngineReplica Replica, PartitionableClient Link) Engine(string id = "e1")
    {
        var link = new PartitionableClient(_store.Client(id == "e1" ? "engine-1" : "engine-2"));
        var replica = new EngineReplica(_directory.File($"replica-{id}.db"), StandaloneStoreFixture.Account, id, link, _keys);
        _replicas.Add(replica);
        return (replica, link);
    }

    private static JsonObject Web(string fields) => Json("{\"name\":\"web\"," + fields + "}");

    private static UpstreamVersion Version(string commit, JsonObject content, string? operation = null) =>
        new(operation ?? "op-" + commit, Url, Path, "main", commit, content);

    private async Task<StoredRecord?> RecordAsync(string id) => await _store.Store.GetAsync(StandaloneStoreFixture.Account, id, default);

    private async Task<JsonObject?> ValueAsync(string id) =>
        await RecordAsync(id) is { Deleted: false } record ? RecordPayload.ValueOf(await OpenAsync(_keys, record)) as JsonObject : null;

    private async Task<string> RevisionsAsync() =>
        string.Join(",", (await _store.Store.FeedAsync(StandaloneStoreFixture.Account, 0, TimeSpan.Zero, 1000, default)).Records
            .OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => $"{r.Id}@{r.Revision}"));

    /// <summary>The local profile as the converter leaves it, tracked from <see cref="Url"/> on <paramref name="base"/>.</summary>
    private async Task<EngineReplica> TrackedAsync(JsonObject local, JsonObject @base)
    {
        var (replica, _) = Engine();
        await replica.WriteProfileAsync(local, "device:pc");
        await replica.SyncAsync();
        var seeded = await replica.ApplyUpstreamAsync("web", Version("c1", @base), new UpstreamBase("c1", @base), "device:pc", [], default);
        Assert.IsType<UpstreamOutcome.Applied>(seeded);
        return replica;
    }

    [Fact]
    public async Task A_new_upstream_version_merges_into_the_local_profile_and_moves_the_base_in_one_batch()
    {
        var v1 = Web("""
            "provisionCommands":["npm install"],"hostPackages":["git"]
            """);
        var replica = await TrackedAsync(Web("""
            "provisionCommands":["npm install"],"hostPackages":["git","jq"]
            """), v1);
        // The seed changed no profile: only the base is stored, with its provenance.
        var applied = (await ValueAsync(EngineReplica.UpstreamAppliedId("web")))!;
        Assert.Equal(Url, applied["url"]!.GetValue<string>());
        Assert.Equal(Path, applied["path"]!.GetValue<string>());
        Assert.Equal("c1", applied["baseCommit"]!.GetValue<string>());
        Assert.True(ProfileRules.Equivalent(v1, applied["baseContent"]));
        var profileRevision = (await RecordAsync(EngineReplica.ProfileId("web")))!.Revision;

        var v2 = Web("""
            "provisionCommands":["npm install","npm run build"],"hostPackages":["git"]
            """);
        var outcome = await replica.ApplyUpstreamAsync("web", Version("c2", v2), null, "device:pc", [], default);

        var merged = Assert.IsType<UpstreamOutcome.Applied>(outcome);
        var profile = (await ValueAsync(EngineReplica.ProfileId("web")))!;
        Assert.Equal("""["npm install","npm run build"]""", profile["provisionCommands"]!.ToJsonString());
        Assert.Equal("""["git","jq"]""", profile["hostPackages"]!.ToJsonString());
        Assert.True(merged.ProfileRevision > profileRevision);
        applied = (await ValueAsync(EngineReplica.UpstreamAppliedId("web")))!;
        Assert.Equal("c2", applied["baseCommit"]!.GetValue<string>());
        Assert.True(ProfileRules.Equivalent(v2, applied["baseContent"]));
        // The profile and the base were one batch: the same store revision range, accepted together.
        Assert.Equal((await RecordAsync(EngineReplica.ProfileId("web")))!.Revision + 1, (await RecordAsync(EngineReplica.UpstreamAppliedId("web")))!.Revision);
        // The replica holds the merge at once, for the VMs' write-back.
        Assert.Equal(profile.ToJsonString(), (await replica.ReadAsync(EngineReplica.ProfileId("web")))!.ToJsonString());

        // The same commit again, or the same operation, changes nothing.
        var before = await RevisionsAsync();
        Assert.IsType<UpstreamOutcome.Unchanged>(await replica.ApplyUpstreamAsync("web", Version("c2", v2, "op-again"), null, "device:pc", [], default));
        Assert.IsType<UpstreamOutcome.Unchanged>(await replica.ApplyUpstreamAsync("web", Version("c3", v2, "op-c2"), null, "device:pc", [], default));
        // A new commit that leaves the file as it was is nothing new either.
        Assert.IsType<UpstreamOutcome.Unchanged>(await replica.ApplyUpstreamAsync("web", Version("c4", v2), null, "device:pc", [], default));
        Assert.Equal(before, await RevisionsAsync());
    }

    [Fact]
    public async Task Both_sides_changing_one_field_is_a_conflict_written_with_the_new_base_and_later_versions_wait_for_it()
    {
        var v1 = Web("""
            "sdks":{"node":"20"},"provisionCommands":["npm install"]
            """);
        var replica = await TrackedAsync(Web("""
            "sdks":{"node":"22"},"provisionCommands":["npm install"]
            """), v1);
        var local = (await ValueAsync(EngineReplica.ProfileId("web")))!;
        var localRevision = (await RecordAsync(EngineReplica.ProfileId("web")))!.Revision;

        var v2 = Web("""
            "sdks":{"node":"18"},"provisionCommands":["npm install","npm test"]
            """);
        var outcome = await replica.ApplyUpstreamAsync("web", Version("c2", v2), null, "device:pc", ["vm:e1/devbox"], default);

        var conflict = Assert.IsType<UpstreamOutcome.Conflict>(outcome);
        Assert.Equal(["sdks.node"], conflict.Fields);
        // The local profile is untouched; the conflict carries the upstream version as ours, the profile as theirs.
        Assert.Equal(localRevision, (await RecordAsync(EngineReplica.ProfileId("web")))!.Revision);
        Assert.Equal(local.ToJsonString(), (await ValueAsync(EngineReplica.ProfileId("web")))!.ToJsonString());
        var open = Assert.Single(await replica.ConflictsAsync());
        Assert.Equal(conflict.ConflictId, open.Id);
        Assert.Equal(EngineReplica.ProfileId("web"), open.ProfileId);
        Assert.Equal(localRevision, open.ProfileRevision);
        Assert.Equal("18", open.Value["ours"]!["sdks"]!["node"]!.GetValue<string>());
        Assert.Equal("22", open.Value["theirs"]!["sdks"]!["node"]!.GetValue<string>());
        Assert.Equal("20", open.Value["base"]!["sdks"]!["node"]!.GetValue<string>());
        // Every other field carries the merge, and the VMs that use the profile hear of it.
        Assert.Equal("""["npm install","npm test"]""", open.Value["merged"]!["provisionCommands"]!.ToJsonString());
        Assert.Contains("vm:e1/devbox", open.Value["identities"]!["ours"]!["origins"]!.AsArray().Select(o => o!.GetValue<string>()));
        Assert.Equal("c2", (await ValueAsync(EngineReplica.UpstreamAppliedId("web")))!["baseCommit"]!.GetValue<string>());

        // Submitted again: nothing new.
        var before = await RevisionsAsync();
        Assert.IsType<UpstreamOutcome.Unchanged>(await replica.ApplyUpstreamAsync("web", Version("c2", v2), null, "device:pc", [], default));
        // A newer version waits while the conflict is open.
        var v3 = Web("""
            "sdks":{"node":"18"},"provisionCommands":["npm install","npm test"],"hostPackages":["jq"]
            """);
        var waiting = Assert.IsType<UpstreamOutcome.Waiting>(await replica.ApplyUpstreamAsync("web", Version("c3", v3), null, "device:pc", [], default));
        Assert.Equal(conflict.ConflictId, waiting.ConflictId);
        Assert.Equal(before, await RevisionsAsync());

        // Resolved (the profile and the close in one batch): the next refresh merges against the base the conflict moved to.
        var resolved = Web("""
            "sdks":{"node":"22"},"provisionCommands":["npm install","npm test"]
            """);
        Assert.IsType<ResolveOutcome.Resolved>(await replica.ResolveAsync(conflict.ConflictId, resolved, "vm:e1/devbox"));
        Assert.IsType<UpstreamOutcome.Applied>(await replica.ApplyUpstreamAsync("web", Version("c3", v3), null, "device:pc", [], default));
        var profile = (await ValueAsync(EngineReplica.ProfileId("web")))!;
        Assert.Equal("22", profile["sdks"]!["node"]!.GetValue<string>());
        Assert.Equal("""["jq"]""", profile["hostPackages"]!.ToJsonString());
        Assert.Empty(await replica.ConflictsAsync());
    }

    [Fact]
    public async Task A_second_device_moving_over_later_does_not_put_its_older_base_over_the_newer_one()
    {
        var v1 = Web("""
            "provisionCommands":["a"]
            """);
        var replica = await TrackedAsync(v1, v1);
        var v2 = Web("""
            "provisionCommands":["a","b"]
            """);
        Assert.IsType<UpstreamOutcome.Applied>(await replica.ApplyUpstreamAsync("web", Version("c2", v2), null, "device:pc", [], default));
        var before = await RevisionsAsync();

        // The other PC's old client stored the base of c1: the engine keeps the base of c2 and the profile as it is.
        Assert.IsType<UpstreamOutcome.Unchanged>(await replica.ApplyUpstreamAsync("web", Version("c1", v1, "seed-laptop"), new UpstreamBase("c1", v1), "device:laptop", [], default));
        Assert.Equal(before, await RevisionsAsync());
        Assert.Equal("""["a","b"]""", (await ValueAsync(EngineReplica.ProfileId("web")))!["provisionCommands"]!.ToJsonString());
    }

    [Fact]
    public async Task A_refresh_whose_answer_was_lost_is_submitted_again_and_leaves_profile_and_base_consistent()
    {
        var (replica, link) = Engine();
        var v1 = Web("""
            "provisionCommands":["a"]
            """);
        await replica.WriteProfileAsync(v1, "device:pc");
        await replica.SyncAsync();
        await replica.ApplyUpstreamAsync("web", Version("c1", v1), new UpstreamBase("c1", v1), "device:pc", [], default);

        var v2 = Web("""
            "provisionCommands":["a","b"]
            """);
        link.LoseNextAnswer = true;
        await Assert.ThrowsAsync<StoreUnavailableException>(() => replica.ApplyUpstreamAsync("web", Version("c2", v2), null, "device:pc", [], default));
        var after = await RevisionsAsync();

        // The device crashed mid-refresh and submits again: the batch went through once, nothing is applied twice.
        Assert.IsType<UpstreamOutcome.Unchanged>(await replica.ApplyUpstreamAsync("web", Version("c2", v2), null, "device:pc", [], default));
        Assert.Equal(after, await RevisionsAsync());
        Assert.Equal("""["a","b"]""", (await ValueAsync(EngineReplica.ProfileId("web")))!["provisionCommands"]!.ToJsonString());
        Assert.Equal("c2", (await ValueAsync(EngineReplica.UpstreamAppliedId("web")))!["baseCommit"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_tracked_file_new_here_is_imported_and_a_profile_deleted_here_stops_being_tracked()
    {
        var (replica, _) = Engine();
        var v1 = Web("""
            "provisionCommands":["a"]
            """);
        Assert.IsType<UpstreamOutcome.Applied>(await replica.ApplyUpstreamAsync("web", Version("c1", v1), null, "device:pc", [], default));
        Assert.Equal("""["a"]""", (await ValueAsync(EngineReplica.ProfileId("web")))!["provisionCommands"]!.ToJsonString());

        // Deleted here: the file is no longer tracked, its base goes, and upstream changes never bring it back.
        await replica.DeleteProfileAsync("web", "device:pc");
        await replica.SyncAsync();
        var v2 = Web("""
            "provisionCommands":["a","b"]
            """);
        Assert.IsType<UpstreamOutcome.Dropped>(await replica.ApplyUpstreamAsync("web", Version("c2", v2), null, "device:pc", [], default));
        Assert.True((await RecordAsync(EngineReplica.UpstreamAppliedId("web")))!.Deleted);
        Assert.True((await RecordAsync(EngineReplica.ProfileId("web")))!.Deleted);
        Assert.IsType<UpstreamOutcome.Dropped>(await replica.ApplyUpstreamAsync("web", Version("c3", v2), null, "device:pc", [], default));
    }

    [Fact]
    public async Task A_profile_deleted_here_stops_being_tracked_also_on_the_same_commit_and_when_its_name_is_created_again()
    {
        var (replica, _) = Engine();
        var v1 = Web("""
            "provisionCommands":["a"]
            """);
        Assert.IsType<UpstreamOutcome.Applied>(await replica.ApplyUpstreamAsync("web", Version("c1", v1), null, "device:pc", [], default));

        // Deleted, then the next refresh still on the same commit: the base goes all the same.
        await replica.DeleteProfileAsync("web", "device:pc");
        await replica.SyncAsync();
        Assert.IsType<UpstreamOutcome.Dropped>(await replica.ApplyUpstreamAsync("web", Version("c1", v1, "op-refresh"), null, "device:pc", [], default));
        Assert.True((await RecordAsync(EngineReplica.UpstreamAppliedId("web")))!.Deleted);

        // Tracked again from a new import, deleted and created again before any refresh: the new profile is not the
        // tracked one, so upstream changes do not reach it, and the base goes with the next refresh.
        var other = replica;
        Assert.IsType<UpstreamOutcome.Applied>(await other.ApplyUpstreamAsync("api", Version("c1", v1), null, "device:pc", [], default));
        await other.DeleteProfileAsync("api", "device:pc");
        await other.SyncAsync();
        var mine = Json("""{"name":"api","provisionCommands":["mine"]}""");
        await other.WriteProfileAsync(mine, "device:pc");
        await other.SyncAsync();
        var v2 = Web("""
            "provisionCommands":["a","b"]
            """);
        Assert.IsType<UpstreamOutcome.Dropped>(await other.ApplyUpstreamAsync("api", Version("c2", v2), null, "device:pc", [], default));
        Assert.Equal("""["mine"]""", (await ValueAsync(EngineReplica.ProfileId("api")))!["provisionCommands"]!.ToJsonString());
        Assert.True((await RecordAsync(EngineReplica.UpstreamAppliedId("api")))!.Deleted);
        // The old client's base, handed over again, predates the deletion: it does not track the new profile either.
        Assert.IsType<UpstreamOutcome.Dropped>(await other.ApplyUpstreamAsync("api", Version("c1", v1, "seed-again"), new UpstreamBase("c1", v1), "device:pc", [], default));
        Assert.Equal("""["mine"]""", (await ValueAsync(EngineReplica.ProfileId("api")))!["provisionCommands"]!.ToJsonString());
    }

    [Fact]
    public async Task A_conflict_one_engine_opened_holds_back_a_newer_version_another_engine_brings_before_its_feed_has_it()
    {
        var v1 = Web("""
            "sdks":{"node":"20"}
            """);
        var first = await TrackedAsync(Web("""
            "sdks":{"node":"22"}
            """), v1);
        var conflict = Assert.IsType<UpstreamOutcome.Conflict>(await first.ApplyUpstreamAsync("web", Version("c2", Web("""
            "sdks":{"node":"18"}
            """)), null, "device:pc", [], default));
        var before = await RevisionsAsync();

        // The other engine has not pulled anything: only the store knows the conflict.
        var (second, _) = Engine("e2");
        var waiting = Assert.IsType<UpstreamOutcome.Waiting>(await second.ApplyUpstreamAsync("web", Version("c3", Web("""
            "sdks":{"node":"18"},"hostPackages":["jq"]
            """)), null, "device:laptop", [], default));
        Assert.Equal(conflict.ConflictId, waiting.ConflictId);
        Assert.Equal(before, await RevisionsAsync());
        Assert.Equal("22", (await ValueAsync(EngineReplica.ProfileId("web")))!["sdks"]!["node"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_import_never_overwrites_a_profile_of_another_provenance_and_an_invalid_upstream_file_changes_nothing()
    {
        var (replica, _) = Engine();
        var mine = Web("""
            "provisionCommands":["mine"]
            """);
        await replica.WriteProfileAsync(mine, "device:pc");
        await replica.SyncAsync();

        // A profile of that name that did not come from this file: refused, never merged without a base.
        var refused = Assert.IsType<UpstreamOutcome.Refused>(await replica.ApplyUpstreamAsync("web", Version("c1", Web("\"provisionCommands\":[\"theirs\"]")), null, "device:pc", [], default));
        Assert.Equal("not-tracked", refused.Code);

        // Tracked from one file: another repository or path is another provenance.
        await replica.ApplyUpstreamAsync("web", Version("c1", mine), new UpstreamBase("c1", mine), "device:pc", [], default);
        var other = Assert.IsType<UpstreamOutcome.Refused>(await replica.ApplyUpstreamAsync("web",
            new UpstreamVersion("op-x", "https://git.example.test/other.git", Path, "main", "c9", mine), null, "device:pc", [], default));
        Assert.Equal("other-provenance", other.Code);

        // Not a valid profile upstream: the local one stays as it is.
        var before = await RevisionsAsync();
        var invalid = Assert.IsType<UpstreamOutcome.Refused>(await replica.ApplyUpstreamAsync("web", Version("c2", Json("""{"name":"web","repos":"nope"}""")), null, "device:pc", [], default));
        Assert.Equal("invalid-profile", invalid.Code);
        Assert.Equal(before, await RevisionsAsync());
        Assert.Equal("""["mine"]""", (await ValueAsync(EngineReplica.ProfileId("web")))!["provisionCommands"]!.ToJsonString());
    }

    [Fact]
    public async Task The_imported_name_is_the_profiles_name_whatever_the_upstream_file_calls_itself()
    {
        var (replica, _) = Engine();
        var renamed = Json("""{"name":"billing","provisionCommands":["a"]}""");
        Assert.IsType<UpstreamOutcome.Applied>(await replica.ApplyUpstreamAsync("billing-2",
            new UpstreamVersion("op-1", Url, "projects/billing.json", null, "c1", renamed), null, "device:pc", [], default));
        Assert.Equal("billing-2", (await ValueAsync(EngineReplica.ProfileId("billing-2")))!["name"]!.GetValue<string>());
    }
}
