using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Constructd.Store.Client;
using Constructd.Store.Records;
using Constructd.Sync.Profiles;
using Constructd.Sync.Sealing;
using Microsoft.Data.Sqlite;

namespace Constructd.Sync.Replica;

/// <summary>A record as the engine's replica holds it, decrypted.</summary>
public sealed record ReplicaRecord(string Id, string Kind, long Revision, bool Deleted, long? DeletedRevision, string Actor, JsonObject? Payload)
{
    public JsonNode? Value => Deleted ? null : RecordPayload.ValueOf(Payload);
}

/// <summary>An open conflict (9.6) as the replica sees it.</summary>
public sealed record ConflictView(string Id, long Revision, string ProfileId, long? ProfileRevision, IReadOnlyList<string> Fields, JsonObject Value);

/// <summary>What a push did.</summary>
public sealed record PushResult(int Sent, int Merged, IReadOnlyList<string> ConflictIds, IReadOnlyList<string> Failed);

/// <summary>The outcome of a resolve.</summary>
public abstract record ResolveOutcome
{
    /// <summary>The profile was written and the conflict closed, in one batch.</summary>
    public sealed record Resolved(long ProfileRevision) : ResolveOutcome;

    /// <summary>Somebody else closed the conflict first; nothing was written.</summary>
    public sealed record AlreadyClosed : ResolveOutcome;

    /// <summary>The profile changed meanwhile and the resolution does not merge with it: a new conflict replaced the old one.</summary>
    public sealed record Recomputed(string ConflictId) : ResolveOutcome;

    /// <summary>The store refused the batch.</summary>
    public sealed record Refused(string Code, string Message) : ResolveOutcome;
}

/// <summary>A profile that does not pass the profile checks.</summary>
public sealed class InvalidProfileException(IReadOnlyList<ProfileProblem> problems)
    : Exception("The profile is not valid: " + string.Join("; ", problems.Select(p => p.Message)))
{
    public IReadOnlyList<ProfileProblem> Problems { get; } = problems;
}

/// <summary>
/// The engine's replica of one account's records, its feed cursor and its outbox, in one SQLite
/// file per engine (9.2, C.4). Engines are the only mergers (S9):
///
/// <list type="bullet">
/// <item>A local profile edit is one transaction: the outbox entry with its <c>writeId</c>, base
/// revision and base content. The edit is acknowledged when that commits, store reachable or not.</item>
/// <item><see cref="PushAsync"/> sends the outbox in order. A <c>412</c> merges ours against the
/// returned record (<see cref="ProfileMerge"/>) and retries on the new base; what does not merge
/// becomes a <c>conflict</c> record that carries the merged value of every other field.</item>
/// <item><see cref="PullAsync"/> applies the feed and advances the cursor in the same transaction.</item>
/// </list>
///
/// The replica keeps payloads decrypted: the engine holds <c>D</c> anyway (8.2).
/// </summary>
public sealed class EngineReplica : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _account;
    private readonly IStoreClient _store;
    private readonly IRecordKeyProvider _keys;
    private readonly int _supported;

    public EngineReplica(string databasePath, string account, string engineId, IStoreClient store, IRecordKeyProvider keys, int supportedPayloadVersion = RecordPayload.CurrentVersion)
    {
        _account = account;
        _store = store;
        _keys = keys;
        _supported = supportedPayloadVersion;
        Actor = StorePrincipal.Engine(account, engineId).Actor;

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        _db.Open();
        Exec(null, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS replica (
                account TEXT NOT NULL, id TEXT NOT NULL, kind TEXT NOT NULL, revision INTEGER NOT NULL,
                deleted INTEGER NOT NULL, deleted_revision INTEGER, actor TEXT NOT NULL, payload TEXT,
                PRIMARY KEY (account, id));
            CREATE TABLE IF NOT EXISTS cursor (account TEXT PRIMARY KEY, since INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS outbox (
                seq INTEGER PRIMARY KEY AUTOINCREMENT, account TEXT NOT NULL, id TEXT NOT NULL, kind TEXT NOT NULL,
                write_id TEXT NOT NULL, base_revision INTEGER, base_value TEXT, value TEXT, deleted INTEGER NOT NULL,
                origins TEXT NOT NULL, failed TEXT);
            """);
    }

    /// <summary>The envelope actor of this engine's writes.</summary>
    public string Actor { get; }

    public static string ProfileId(string name) => "profile:" + name;

    public void Dispose()
    {
        _db.Dispose();
        _lock.Dispose();
    }

    // ---- local reads ----------------------------------------------------------------------------

    /// <summary>The local view of a record's value: the latest queued edit, else the replica.</summary>
    public async Task<JsonNode?> ReadAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { return LocalValue(id).Value; }
        finally { _lock.Release(); }
    }

    public async Task<ReplicaRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { return Replica(id); }
        finally { _lock.Release(); }
    }

    public async Task<long> CursorAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { return Cursor(); }
        finally { _lock.Release(); }
    }

    public async Task<int> PendingAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { return Convert.ToInt32(Scalar("SELECT COUNT(*) FROM outbox WHERE account = $a AND failed IS NULL", ("$a", _account)), CultureInfo.InvariantCulture); }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<ConflictView>> ConflictsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var result = new List<ConflictView>();
            foreach (var record in Replicas($"SELECT {ReplicaColumns} FROM replica WHERE account = $a AND kind = 'conflict' AND deleted = 0 ORDER BY revision", ("$a", _account)))
            {
                if (record.Value is JsonObject value) result.Add(ToConflictView(record.Id, record.Revision, value));
            }

            return result;
        }
        finally { _lock.Release(); }
    }

    // ---- local writes ---------------------------------------------------------------------------

    /// <summary>
    /// Queues a profile edit. Acknowledged once this returns: the outbox entry, its base revision and
    /// its base content are committed together. <paramref name="origin"/> names the VM or device the
    /// edit came from (9.1 <c>origins</c>).
    /// </summary>
    public Task WriteProfileAsync(JsonObject profile, string? origin, CancellationToken ct = default)
    {
        var problems = ProfileRules.Validate(profile);
        if (problems.Count > 0) throw new InvalidProfileException(problems);
        var normalized = ProfileRules.Finish(ProfileRules.Normalize(profile));
        return EnqueueAsync(ProfileId(ProfileRules.Text(profile["name"])), normalized, deleted: false, origin, ct);
    }

    /// <summary>Queues the deletion of a profile (an explicit delete; 9.5).</summary>
    public Task DeleteProfileAsync(string name, string? origin, CancellationToken ct = default) =>
        EnqueueAsync(ProfileId(name), null, deleted: true, origin, ct);

    private async Task EnqueueAsync(string id, JsonObject? value, bool deleted, string? origin, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var replica = Replica(id);
            var version = RecordPayload.VersionOf(replica?.Payload);
            if (version > _supported) throw new PayloadTooNewException(version, _supported);

            var (local, pending) = LocalValue(id);
            if (deleted && local is null) return;

            // The first queued edit of a record is based on the replica's revision; a later one on
            // the edit before it, whose revision is known only once the store accepted it.
            long? baseRevision = pending ? null : replica?.Revision ?? 0;
            using var tx = _db.BeginTransaction();
            Exec(tx, "INSERT INTO outbox (account, id, kind, write_id, base_revision, base_value, value, deleted, origins) VALUES ($a, $id, $k, $w, $br, $bv, $v, $d, $o)",
                ("$a", _account), ("$id", id), ("$k", RecordKinds.Profile), ("$w", NewWriteId()), ("$br", baseRevision),
                ("$bv", local?.ToJsonString()), ("$v", value?.ToJsonString()), ("$d", deleted ? 1 : 0),
                ("$o", Origins(origin is null ? [] : [origin])));
            tx.Commit();
        }
        finally { _lock.Release(); }
    }

    // ---- push -----------------------------------------------------------------------------------

    /// <summary>
    /// Sends the outbox in order until it is empty. A store that cannot be reached ends the push
    /// with the rest still queued (<see cref="StoreUnavailableException"/> propagates).
    /// </summary>
    public async Task<PushResult> PushAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            int sent = 0, merged = 0;
            var conflicts = new List<string>();
            var failed = new List<string>();
            long epoch = 0;
            byte[] key = [];
            while (NextEntry() is { } entry)
            {
                if (key.Length == 0)
                {
                    epoch = await _store.ActiveEpochAsync(ct);
                    key = await _keys.GetKeyAsync(_account, epoch, ct);
                }

                var step = await SendAsync(entry, epoch, key, ct);
                switch (step)
                {
                    case Step.Sent: sent++; break;
                    case Step.Merged: merged++; break;
                    case Step.Conflict c: conflicts.Add(c.ConflictId); break;
                    case Step.Failed f: failed.Add(f.Reason); break;
                }
            }

            return new PushResult(sent, merged, conflicts, failed);
        }
        finally { _lock.Release(); }
    }

    private abstract record Step
    {
        public sealed record Sent : Step;
        public sealed record Merged : Step;
        public sealed record Dropped : Step;
        public sealed record Conflict(string ConflictId) : Step;
        public sealed record Failed(string Reason) : Step;
    }

    private async Task<Step> SendAsync(OutboxEntry entry, long epoch, byte[] key, CancellationToken ct)
    {
        var replica = Replica(entry.Id);
        if (entry.BaseRevision is not { } baseRevision)
        {
            // The edit before this one was accepted: base on what the replica holds now. Persisted
            // before sending so a retry after a lost answer carries the same envelope.
            baseRevision = replica?.Revision ?? 0;
            Exec(null, "UPDATE outbox SET base_revision = $br WHERE seq = $s", ("$br", baseRevision), ("$s", entry.Seq));
            entry = entry with { BaseRevision = baseRevision };
        }

        // The edit before this one was merged, or the feed brought that revision with other content:
        // merge before sending instead of overwriting it.
        if (replica is not null && replica.Revision == baseRevision && !ProfileMerge.Same(replica.Value, entry.BaseValue))
        {
            return await ReconcileAsync(entry, replica, epoch, key, ct);
        }

        JsonObject payload;
        try
        {
            payload = RecordPayload.Rewrite(replica is { Deleted: false } ? replica.Payload : null, entry.Value, entry.Origins, _supported);
        }
        catch (PayloadTooNewException ex)
        {
            return Fail(entry, ex.Message);
        }

        var envelope = new RecordEnvelope(entry.Id, entry.Kind, epoch, Actor, baseRevision, entry.WriteId, entry.Deleted);
        var write = new RecordWrite(envelope.Id, envelope.Kind, envelope.Epoch, envelope.Actor, envelope.BaseRevision, envelope.WriteId, envelope.Deleted,
            RecordSealer.Seal(key, envelope, payload));

        switch (await _store.WriteAsync(write, ct))
        {
            case WriteOutcome.Accepted accepted:
                using (var tx = _db.BeginTransaction())
                {
                    Exec(tx, "DELETE FROM outbox WHERE seq = $s", ("$s", entry.Seq));
                    Upsert(tx, accepted.Record, payload);
                    tx.Commit();
                }

                return new Step.Sent();

            case WriteOutcome.Stale stale:
                if (stale.Conflict.Current is not { } current)
                {
                    // The record is gone from the store altogether: nobody else's change to keep.
                    return Rebase(entry, null, null) ? new Step.Merged() : new Step.Dropped();
                }

                var theirs = await OpenAsync(current, ct);
                Upsert(null, current, theirs);
                return await ReconcileAsync(entry, ToReplica(current, theirs), epoch, key, ct);

            case WriteOutcome.Refused refused:
                return Fail(entry, $"{refused.Code}: {refused.Message}");

            default:
                throw new InvalidOperationException();
        }
    }

    /// <summary>Ours did not apply on <paramref name="theirs"/>: merge, or record the conflict.</summary>
    private async Task<Step> ReconcileAsync(OutboxEntry entry, ReplicaRecord theirs, long epoch, byte[] key, CancellationToken ct)
    {
        if (RecordPayload.VersionOf(theirs.Payload) > _supported)
            return Fail(entry, new PayloadTooNewException(RecordPayload.VersionOf(theirs.Payload), _supported).Message);

        var origins = entry.Origins.Concat(RecordPayload.OriginsOf(theirs.Payload)).ToList();
        if (entry.Deleted)
        {
            if (theirs.Deleted) return Drop(entry);
            if (ProfileMerge.Same(theirs.Value, entry.BaseValue))
            {
                // Theirs did not change the content: the deletion still holds.
                Rebase(entry with { Origins = origins }, theirs.Revision, theirs.Value);
                return new Step.Merged();
            }

            return await ConflictAsync(entry, theirs, null, ["deleted"], epoch, key, ct);
        }

        // 9.5: an edit of a deleted profile, or one based on a revision before a deletion, is a
        // conflict, also when the name was created again since.
        var baseRevision = entry.BaseRevision ?? 0;
        if (theirs.Deleted || (theirs.DeletedRevision is { } deletedAt && deletedAt > baseRevision && entry.BaseValue is not null))
            return await ConflictAsync(entry, theirs, null, ["deleted"], epoch, key, ct);

        var result = ProfileMerge.Merge(entry.BaseValue as JsonObject, (JsonObject)entry.Value!, (JsonObject)theirs.Value!);
        if (!result.Clean) return await ConflictAsync(entry, theirs, result, result.Conflicts, epoch, key, ct);

        if (ProfileMerge.Same(result.Merged, theirs.Value) && origins.All(RecordPayload.OriginsOf(theirs.Payload).Contains)) return Drop(entry);
        Rebase(entry with { Value = result.Merged, Origins = origins }, theirs.Revision, theirs.Value);
        return new Step.Merged();
    }

    /// <summary>
    /// Writes the conflict record for this entry and drops the entry. The id comes from the profile,
    /// base, ours and theirs, so the same conflict is written once however often it is computed.
    /// </summary>
    private async Task<Step> ConflictAsync(OutboxEntry entry, ReplicaRecord theirs, ProfileMergeResult? merge, IReadOnlyList<string> fields, long epoch, byte[] key, CancellationToken ct)
    {
        var value = ConflictValue(entry.Id, theirs.Revision, entry.BaseValue, entry.Deleted ? null : entry.Value, theirs.Value,
            merge?.Merged, fields, Actor, entry.Origins, theirs.Actor, RecordPayload.OriginsOf(theirs.Payload));
        var conflictId = ConflictId(entry.Id, entry.BaseValue, entry.Deleted ? null : entry.Value, theirs.Value);
        var written = await WriteConflictAsync(conflictId, value, entry.Origins.Concat(RecordPayload.OriginsOf(theirs.Payload)), epoch, key, ct);
        if (written is WriteOutcome.Refused refused) return Fail(entry, $"{refused.Code}: {refused.Message}");
        Exec(null, "DELETE FROM outbox WHERE seq = $s", ("$s", entry.Seq));
        return new Step.Conflict(conflictId);
    }

    private async Task<WriteOutcome> WriteConflictAsync(string conflictId, JsonObject value, IEnumerable<string> origins, long epoch, byte[] key, CancellationToken ct)
    {
        var payload = RecordPayload.Rewrite(null, value, origins, _supported);
        var envelope = new RecordEnvelope(conflictId, RecordKinds.Conflict, epoch, Actor, 0, NewWriteId(), false);
        var outcome = await _store.WriteAsync(new RecordWrite(envelope.Id, envelope.Kind, envelope.Epoch, envelope.Actor, 0, envelope.WriteId, false,
            RecordSealer.Seal(key, envelope, payload)), ct);
        if (outcome is WriteOutcome.Accepted accepted) Upsert(null, accepted.Record, payload);
        return outcome;
    }

    // ---- pull -----------------------------------------------------------------------------------

    /// <summary>Applies one page of the feed; the cursor advances in the same transaction.</summary>
    public async Task<int> PullAsync(TimeSpan wait = default, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var page = await _store.FeedAsync(Cursor(), wait, ct);
            var opened = new List<(StoredRecord Record, JsonObject? Payload)>();
            foreach (var record in page.Records) opened.Add((record, await OpenAsync(record, ct)));

            using var tx = _db.BeginTransaction();
            foreach (var (record, payload) in opened) Upsert(tx, record, payload);
            Exec(tx, "INSERT INTO cursor (account, since) VALUES ($a, $c) ON CONFLICT (account) DO UPDATE SET since = MAX(since, $c)", ("$a", _account), ("$c", page.Cursor));
            tx.Commit();
            return page.Records.Count;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Push, then pull until the feed is drained.</summary>
    public async Task<PushResult> SyncAsync(CancellationToken ct = default)
    {
        var pushed = await PushAsync(ct);
        while (await PullAsync(default, ct) > 0) { }
        return pushed;
    }

    // ---- resolve --------------------------------------------------------------------------------

    /// <summary>
    /// Resolves a conflict (9.6): writes the profile with <c>If-Match</c> on its current revision and
    /// closes the conflict in one batch. No claims: when another resolver closed it first, the batch
    /// fails and this returns <see cref="ResolveOutcome.AlreadyClosed"/>. When the profile changed
    /// meanwhile, the resolution is merged with the change, or the conflict is computed again.
    /// Online: the store must be reachable.
    /// </summary>
    public async Task<ResolveOutcome> ResolveAsync(string conflictId, JsonObject resolved, string? origin, CancellationToken ct = default)
    {
        var problems = ProfileRules.Validate(resolved);
        if (problems.Count > 0) throw new InvalidProfileException(problems);
        resolved = ProfileRules.Finish(ProfileRules.Normalize(resolved));

        await _lock.WaitAsync(ct);
        try
        {
            var epoch = await _store.ActiveEpochAsync(ct);
            var key = await _keys.GetKeyAsync(_account, epoch, ct);
            if (await _store.GetAsync(conflictId, ct) is not { Deleted: false } conflictRecord) return new ResolveOutcome.AlreadyClosed();
            var conflictPayload = (await OpenAsync(conflictRecord, ct))!;
            var conflict = ToConflictView(conflictId, conflictRecord.Revision, (JsonObject)RecordPayload.ValueOf(conflictPayload)!);
            if (ProfileRules.Text(resolved["name"]) is var name && ProfileId(name) != conflict.ProfileId)
                throw new InvalidProfileException([new ProfileProblem("name", "The resolution is for another profile.")]);

            var origins = RecordPayload.OriginsOf(conflictPayload).Concat(origin is null ? [] : [origin]).ToList();
            var seen = conflict.Value["theirs"]?.DeepClone();
            var profile = await _store.GetAsync(conflict.ProfileId, ct);

            for (var attempt = 0; attempt < 5; attempt++)
            {
                var currentPayload = profile is null ? null : await OpenAsync(profile, ct);
                var currentValue = profile is { Deleted: false } ? RecordPayload.ValueOf(currentPayload) : null;

                // The profile moved on since the conflict was computed: carry the resolution over.
                if ((profile?.Revision ?? 0) != (conflict.ProfileRevision ?? 0) && !ProfileMerge.Same(currentValue, seen))
                {
                    var carried = currentValue is JsonObject current && seen is JsonObject seenObject
                        ? ProfileMerge.Merge(seenObject, resolved, current)
                        : null;
                    if (carried is not { Clean: true })
                        return await RecomputeAsync(conflictRecord, conflict, seen, resolved, profile, currentPayload, origins, epoch, key, ct);
                    resolved = carried.Merged;
                }

                var profileWrite = Sealed(key, conflict.ProfileId, RecordKinds.Profile, epoch, profile?.Revision ?? 0, false,
                    RecordPayload.Rewrite(profile is { Deleted: false } ? currentPayload : null, resolved, origins, _supported));
                var close = Sealed(key, conflictId, RecordKinds.Conflict, epoch, conflictRecord.Revision, true, RecordPayload.Rewrite(null, null, origins, _supported));

                switch (await _store.BatchAsync([profileWrite.Write, close.Write], ct))
                {
                    case BatchOutcome.Accepted accepted:
                        using (var tx = _db.BeginTransaction())
                        {
                            Upsert(tx, accepted.Records[0], profileWrite.Payload);
                            Upsert(tx, accepted.Records[1], null);
                            tx.Commit();
                        }

                        return new ResolveOutcome.Resolved(accepted.Records[0].Revision);

                    case BatchOutcome.Stale stale:
                        if (stale.Conflict.Conflicts.Any(c => c.Id == conflictId)) return new ResolveOutcome.AlreadyClosed();
                        seen = profile is { Deleted: false } ? currentValue?.DeepClone() : null;
                        conflict = conflict with { ProfileRevision = profile?.Revision };
                        profile = stale.Conflict.Conflicts.Single(c => c.Id == conflict.ProfileId).Current;
                        break;

                    case BatchOutcome.Refused refused:
                        return new ResolveOutcome.Refused(refused.Code, refused.Message);
                }
            }

            throw new StoreUnavailableException("The profile kept changing while the conflict was being resolved.");
        }
        finally { _lock.Release(); }
    }

    /// <summary>The resolution does not merge with the profile's new state: close this conflict and open the new one, in one batch.</summary>
    private async Task<ResolveOutcome> RecomputeAsync(StoredRecord conflictRecord, ConflictView conflict, JsonNode? seen, JsonObject resolved,
        StoredRecord? profile, JsonObject? profilePayload, List<string> origins, long epoch, byte[] key, CancellationToken ct)
    {
        var theirsValue = profile is { Deleted: false } ? RecordPayload.ValueOf(profilePayload) : null;
        var merge = theirsValue is JsonObject t && seen is JsonObject s ? ProfileMerge.Merge(s, resolved, t) : null;
        var fields = merge?.Conflicts ?? ["deleted"];
        var newId = ConflictId(conflict.ProfileId, seen, resolved, theirsValue);
        var value = ConflictValue(conflict.ProfileId, profile?.Revision, seen, resolved, theirsValue, merge?.Merged, fields,
            Actor, origins, profile?.Actor, RecordPayload.OriginsOf(profilePayload));
        var open = Sealed(key, newId, RecordKinds.Conflict, epoch, 0, false, RecordPayload.Rewrite(null, value, origins, _supported));
        var close = Sealed(key, conflictRecord.Id, RecordKinds.Conflict, epoch, conflictRecord.Revision, true, RecordPayload.Rewrite(null, null, origins, _supported));
        return await _store.BatchAsync([open.Write, close.Write], ct) switch
        {
            BatchOutcome.Accepted => new ResolveOutcome.Recomputed(newId),
            BatchOutcome.Stale stale when stale.Conflict.Conflicts.Any(c => c.Id == conflictRecord.Id) => new ResolveOutcome.AlreadyClosed(),
            BatchOutcome.Stale => new ResolveOutcome.Recomputed(newId),
            BatchOutcome.Refused refused => new ResolveOutcome.Refused(refused.Code, refused.Message),
            _ => throw new InvalidOperationException(),
        };
    }

    // ---- conflict records -----------------------------------------------------------------------

    /// <summary>Same profile, base, ours and theirs: same id (C.5).</summary>
    public static string ConflictId(string profileId, JsonNode? baseValue, JsonNode? ours, JsonNode? theirs)
    {
        var inputs = new JsonArray(JsonValue.Create(profileId), Canonical(baseValue), Canonical(ours), Canonical(theirs));
        return "conflict:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputs.ToJsonString())))[..32].ToLowerInvariant();
    }

    private static JsonObject ConflictValue(string profileId, long? profileRevision, JsonNode? baseValue, JsonNode? ours, JsonNode? theirs,
        JsonObject? merged, IReadOnlyList<string> fields, string oursActor, IEnumerable<string> oursOrigins, string? theirsActor, IEnumerable<string> theirsOrigins) => new()
    {
        ["profile"] = profileId,
        ["profileRevision"] = profileRevision,
        ["fields"] = new JsonArray(fields.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
        ["base"] = baseValue?.DeepClone(),
        ["ours"] = ours?.DeepClone(),
        ["theirs"] = theirs?.DeepClone(),
        ["merged"] = merged?.DeepClone(),
        ["identities"] = new JsonObject
        {
            ["ours"] = new JsonObject { ["actor"] = oursActor, ["origins"] = new JsonArray(oursOrigins.Select(o => (JsonNode)JsonValue.Create(o)!).ToArray()) },
            ["theirs"] = new JsonObject { ["actor"] = theirsActor, ["origins"] = new JsonArray(theirsOrigins.Select(o => (JsonNode)JsonValue.Create(o)!).ToArray()) },
        },
    };

    private static ConflictView ToConflictView(string id, long revision, JsonObject value) => new(
        id,
        revision,
        ProfileRules.Text(value["profile"]),
        value["profileRevision"] is JsonValue r && r.TryGetValue<long>(out var rev) ? rev : null,
        (value["fields"] as JsonArray ?? []).Select(f => f?.GetValue<string>() ?? "").ToList(),
        value);

    // ---- internals ------------------------------------------------------------------------------

    private sealed record OutboxEntry(long Seq, string Id, string Kind, string WriteId, long? BaseRevision, JsonNode? BaseValue, JsonNode? Value, bool Deleted, IReadOnlyList<string> Origins);

    private const string ReplicaColumns = "id, kind, revision, deleted, deleted_revision, actor, payload";

    private OutboxEntry? NextEntry()
    {
        using var command = Command(null, """
            SELECT seq, id, kind, write_id, base_revision, base_value, value, deleted, origins FROM outbox o
            WHERE account = $a AND failed IS NULL
              AND NOT EXISTS (SELECT 1 FROM outbox f WHERE f.account = o.account AND f.id = o.id AND f.failed IS NOT NULL AND f.seq < o.seq)
            ORDER BY seq LIMIT 1
            """, ("$a", _account));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new OutboxEntry(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : JsonNode.Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : JsonNode.Parse(reader.GetString(6)),
            reader.GetInt64(7) != 0,
            (JsonNode.Parse(reader.GetString(8)) as JsonArray ?? []).Select(o => o!.GetValue<string>()).ToList());
    }

    /// <summary>Puts the entry on a new base with a new write id. False when there is nothing left to write.</summary>
    private bool Rebase(OutboxEntry entry, long? baseRevision, JsonNode? baseValue)
    {
        if (entry.Deleted && baseRevision is null) { Drop(entry); return false; }
        Exec(null, "UPDATE outbox SET base_revision = $br, base_value = $bv, value = $v, origins = $o, write_id = $w WHERE seq = $s",
            ("$br", baseRevision ?? 0), ("$bv", baseValue?.ToJsonString()), ("$v", entry.Value?.ToJsonString()),
            ("$o", Origins(entry.Origins)), ("$w", NewWriteId()), ("$s", entry.Seq));
        return true;
    }

    private Step.Dropped Drop(OutboxEntry entry)
    {
        Exec(null, "DELETE FROM outbox WHERE seq = $s", ("$s", entry.Seq));
        return new Step.Dropped();
    }

    private Step.Failed Fail(OutboxEntry entry, string reason)
    {
        Exec(null, "UPDATE outbox SET failed = $f WHERE seq = $s", ("$f", reason), ("$s", entry.Seq));
        return new Step.Failed($"{entry.Id}: {reason}");
    }

    private (JsonNode? Value, bool Pending) LocalValue(string id)
    {
        using var command = Command(null, "SELECT value, deleted FROM outbox WHERE account = $a AND id = $id AND failed IS NULL ORDER BY seq DESC LIMIT 1", ("$a", _account), ("$id", id));
        using var reader = command.ExecuteReader();
        if (reader.Read()) return (reader.GetInt64(1) != 0 || reader.IsDBNull(0) ? null : JsonNode.Parse(reader.GetString(0)), true);
        return (Replica(id)?.Value?.DeepClone(), false);
    }

    private ReplicaRecord? Replica(string id) =>
        Replicas($"SELECT {ReplicaColumns} FROM replica WHERE account = $a AND id = $id", ("$a", _account), ("$id", id)).FirstOrDefault();

    private List<ReplicaRecord> Replicas(string sql, params (string, object?)[] parameters)
    {
        using var command = Command(null, sql, parameters);
        using var reader = command.ExecuteReader();
        var result = new List<ReplicaRecord>();
        while (reader.Read())
        {
            result.Add(new ReplicaRecord(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3) != 0,
                reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : JsonNode.Parse(reader.GetString(6)) as JsonObject));
        }

        return result;
    }

    private static ReplicaRecord ToReplica(StoredRecord record, JsonObject? payload) =>
        new(record.Id, record.Kind, record.Revision, record.Deleted, record.DeletedRevision, record.Actor, payload);

    /// <summary>Stores a record the store returned, unless the replica already holds a newer revision.</summary>
    private void Upsert(SqliteTransaction? tx, StoredRecord record, JsonObject? payload)
    {
        Exec(tx, """
            INSERT INTO replica (account, id, kind, revision, deleted, deleted_revision, actor, payload) VALUES ($a, $id, $k, $r, $d, $dr, $actor, $p)
            ON CONFLICT (account, id) DO UPDATE SET kind = $k, revision = $r, deleted = $d, deleted_revision = $dr, actor = $actor, payload = $p
            WHERE excluded.revision > replica.revision
            """,
            ("$a", _account), ("$id", record.Id), ("$k", record.Kind), ("$r", record.Revision), ("$d", record.Deleted ? 1 : 0),
            ("$dr", record.DeletedRevision), ("$actor", record.Actor), ("$p", record.Deleted ? null : payload?.ToJsonString()));
    }

    /// <summary>Opens a stored record's payload; a tombstone or a record without payload has none.</summary>
    private async Task<JsonObject?> OpenAsync(StoredRecord record, CancellationToken ct)
    {
        if (record.Deleted || record.Ciphertext is not { Length: > 0 } sealedBytes) return null;
        var key = await _keys.GetKeyAsync(_account, record.Epoch, ct);
        return RecordSealer.Open(key, record.Envelope, sealedBytes);
    }

    private (RecordWrite Write, JsonObject Payload) Sealed(byte[] key, string id, string kind, long epoch, long baseRevision, bool deleted, JsonObject payload, string? writeId = null)
    {
        var envelope = new RecordEnvelope(id, kind, epoch, Actor, baseRevision, writeId ?? NewWriteId(), deleted);
        return (new RecordWrite(id, kind, epoch, Actor, baseRevision, envelope.WriteId, deleted, RecordSealer.Seal(key, envelope, payload)), payload);
    }

    private long Cursor() => Scalar("SELECT since FROM cursor WHERE account = $a", ("$a", _account)) is { } value and not DBNull
        ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
        : 0;

    private static string NewWriteId() => Guid.NewGuid().ToString("n");

    private static string Origins(IEnumerable<string> origins) =>
        new JsonArray(origins.Distinct(StringComparer.Ordinal).Select(o => (JsonNode)JsonValue.Create(o)!).ToArray()).ToJsonString();

    /// <summary>Object keys in ordinal order, recursively, so equal JSON hashes equally.</summary>
    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Canonical(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Canonical).ToArray()),
        _ => node?.DeepClone(),
    };

    private object? Scalar(string sql, params (string, object?)[] parameters)
    {
        using var command = Command(null, sql, parameters);
        return command.ExecuteScalar();
    }

    private void Exec(SqliteTransaction? tx, string sql, params (string, object?)[] parameters)
    {
        using var command = Command(tx, sql, parameters);
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _db.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
