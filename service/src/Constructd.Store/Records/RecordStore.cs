using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Constructd.Store.Records;

/// <summary>The outcome of a single write.</summary>
public abstract record WriteOutcome
{
    /// <summary>Accepted; <paramref name="Record"/> carries the assigned revision.</summary>
    public sealed record Accepted(StoredRecord Record) : WriteOutcome;

    /// <summary>The base was stale: <c>412</c> with the current record.</summary>
    public sealed record Stale(WriteConflict Conflict) : WriteOutcome;

    /// <summary>Refused for another reason; never retried unchanged.</summary>
    public sealed record Refused(int Status, string Code, string Message) : WriteOutcome;
}

/// <summary>The outcome of an atomic batch.</summary>
public abstract record BatchOutcome
{
    public sealed record Accepted(IReadOnlyList<StoredRecord> Records) : BatchOutcome;

    public sealed record Stale(BatchConflict Conflict) : BatchOutcome;

    public sealed record Refused(int Status, string Code, string Message) : BatchOutcome;
}

/// <summary>
/// The record store (9.2, 9.5) over its own SQLite file. One connection, one lock: the store is a
/// single process and its writes are small, so serialising every operation is the simplest thing
/// that keeps compare-and-swap, batches and the revision counter atomic.
///
/// Revisions come from one counter for the whole store, so a revision is also a feed cursor. Every
/// accepted write is kept as a version for the history (20 versions or 90 days, whichever keeps
/// more); the current row of a record carries the revision of its latest deletion for good.
/// </summary>
public sealed class RecordStore : IDisposable
{
    public const int HistoryVersions = 20;
    public static readonly TimeSpan HistoryAge = TimeSpan.FromDays(90);
    public const int MaxIdLength = 512;

    private const string Columns = "id, kind, epoch, actor, base_revision, write_id, deleted, ciphertext, revision, stored_at";

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TimeProvider _time;
    private TaskCompletionSource _changed = NewSignal();

    /// <param name="databasePath">The SQLite file; <c>:memory:</c> keeps the store in memory.</param>
    public RecordStore(string databasePath, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        if (databasePath != ":memory:")
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _connection.Open();
        Execute("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS store_counter (one INTEGER PRIMARY KEY CHECK (one = 1), revision INTEGER NOT NULL);
            INSERT OR IGNORE INTO store_counter (one, revision) VALUES (1, 0);
            CREATE TABLE IF NOT EXISTS store_epochs (account TEXT PRIMARY KEY, active INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS store_records (
                account TEXT NOT NULL, id TEXT NOT NULL, kind TEXT NOT NULL, epoch INTEGER NOT NULL,
                actor TEXT NOT NULL, base_revision INTEGER NOT NULL, write_id TEXT NOT NULL,
                deleted INTEGER NOT NULL, ciphertext BLOB, revision INTEGER NOT NULL, stored_at TEXT NOT NULL,
                deleted_revision INTEGER,
                PRIMARY KEY (account, id));
            CREATE INDEX IF NOT EXISTS store_records_feed ON store_records (account, revision);
            CREATE TABLE IF NOT EXISTS store_versions (
                account TEXT NOT NULL, id TEXT NOT NULL, kind TEXT NOT NULL, epoch INTEGER NOT NULL,
                actor TEXT NOT NULL, base_revision INTEGER NOT NULL, write_id TEXT NOT NULL,
                deleted INTEGER NOT NULL, ciphertext BLOB, revision INTEGER NOT NULL, stored_at TEXT NOT NULL,
                PRIMARY KEY (account, revision));
            CREATE INDEX IF NOT EXISTS store_versions_id ON store_versions (account, id, revision);
            CREATE INDEX IF NOT EXISTS store_versions_write ON store_versions (account, write_id);
            """);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _lock.Dispose();
    }

    // ---- reads ----------------------------------------------------------------------------------

    public async Task<StoredRecord?> GetAsync(string account, string id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { return Current(account, id); }
        finally { _lock.Release(); }
    }

    /// <summary>Past versions, newest first, without the deletion revision (that lives on the current row).</summary>
    public async Task<IReadOnlyList<StoredRecord>> HistoryAsync(string account, string id, int limit, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Query($"SELECT {Columns}, NULL FROM store_versions WHERE account = $a AND id = $id ORDER BY revision DESC LIMIT $limit",
                ("$a", account), ("$id", id), ("$limit", Math.Clamp(limit, 1, 1000)));
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Current records whose revision is after <paramref name="since"/>, oldest first. With a
    /// <paramref name="wait"/>, an empty answer waits for the next accepted write (long-poll).
    /// </summary>
    public async Task<FeedPage> FeedAsync(string account, long since, TimeSpan wait, int limit, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + wait;
        while (true)
        {
            Task changed;
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var records = Query($"SELECT {Columns}, deleted_revision FROM store_records WHERE account = $a AND revision > $since ORDER BY revision LIMIT $limit",
                    ("$a", account), ("$since", since), ("$limit", Math.Clamp(limit, 1, 1000)));
                if (records.Count > 0) return new FeedPage(records, records[^1].Revision);
                changed = _changed.Task;
            }
            finally { _lock.Release(); }

            var left = deadline - _time.GetUtcNow();
            if (left <= TimeSpan.Zero) return new FeedPage([], since);
            await Task.WhenAny(changed, Task.Delay(left, _time, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!changed.IsCompleted) return new FeedPage([], since);
        }
    }

    public async Task<long> ActiveEpochAsync(string account, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { return ActiveEpoch(account); }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Sets the account's active epoch. The epoch routes and rotation are later work (8.4); this is
    /// the hook they and the tests use. Every account starts at epoch 1.
    /// </summary>
    public async Task SetActiveEpochAsync(string account, long epoch, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var command = Command("INSERT INTO store_epochs (account, active) VALUES ($a, $e) ON CONFLICT (account) DO UPDATE SET active = $e",
                ("$a", account), ("$e", epoch));
            command.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    // ---- writes ---------------------------------------------------------------------------------

    public async Task<WriteOutcome> WriteAsync(StorePrincipal principal, RecordWrite write, CancellationToken ct)
    {
        var outcome = await BatchAsync(principal, [write], ct).ConfigureAwait(false);
        return outcome switch
        {
            BatchOutcome.Accepted accepted => new WriteOutcome.Accepted(accepted.Records[0]),
            BatchOutcome.Stale stale => new WriteOutcome.Stale(stale.Conflict.Conflicts[0]),
            BatchOutcome.Refused refused => new WriteOutcome.Refused(refused.Status, refused.Code, refused.Message),
            _ => throw new InvalidOperationException(),
        };
    }

    /// <summary>
    /// Applies every write or none. Each is checked against the principal, the write table and the
    /// active epoch, then compared with the record's current revision. A write whose
    /// <c>writeId</c> the store already accepted is answered with that accepted version, so an
    /// engine that lost the answer can simply send it again.
    /// </summary>
    public async Task<BatchOutcome> BatchAsync(StorePrincipal principal, IReadOnlyList<RecordWrite> writes, CancellationToken ct)
    {
        if (writes.Count == 0) return new BatchOutcome.Refused(400, "empty-batch", "A batch needs at least one write.");
        if (writes.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count() != writes.Count)
            return new BatchOutcome.Refused(400, "duplicate-id", "A batch writes each record once.");
        if (writes.Select(w => w.WriteId).Distinct(StringComparer.Ordinal).Count() != writes.Count)
            return new BatchOutcome.Refused(400, "duplicate-write-id", "Each write in a batch needs its own writeId.");

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var epoch = ActiveEpoch(principal.Account);
            foreach (var write in writes)
            {
                if (Check(principal, write, epoch) is { } refusal) return refusal;
            }

            // Replays: all of them, or none.
            var replayed = writes.Select(w => Replayed(principal.Account, w)).ToList();
            if (replayed.Any(r => r.Refusal is not null)) return replayed.First(r => r.Refusal is not null).Refusal!;
            if (replayed.All(r => r.Record is not null)) return new BatchOutcome.Accepted(replayed.Select(r => r.Record!).ToList());
            if (replayed.Any(r => r.Record is not null))
                return new BatchOutcome.Refused(409, "partial-replay", "Some writes of this batch were accepted earlier and some were not.");

            var conflicts = new List<WriteConflict>();
            var currents = new List<StoredRecord?>();
            foreach (var write in writes)
            {
                var current = Current(principal.Account, write.Id);
                currents.Add(current);
                if ((current?.Revision ?? 0) != write.BaseRevision) conflicts.Add(new WriteConflict(write.Id, current));
            }

            if (conflicts.Count > 0) return new BatchOutcome.Stale(new BatchConflict(conflicts));

            for (var i = 0; i < writes.Count; i++)
            {
                if (currents[i] is { } current && current.Kind != writes[i].Kind)
                    return new BatchOutcome.Refused(409, "kind-mismatch", $"Record '{writes[i].Id}' is a {current.Kind} record.");
                if (currents[i] is null && writes[i].Deleted)
                    return new BatchOutcome.Refused(404, "not-found", $"Record '{writes[i].Id}' does not exist.");
            }

            var accepted = new List<StoredRecord>();
            using (var tx = _connection.BeginTransaction())
            {
                var now = _time.GetUtcNow();
                for (var i = 0; i < writes.Count; i++)
                {
                    accepted.Add(Apply(tx, principal.Account, writes[i], currents[i], now));
                }

                tx.Commit();
            }

            var signal = Interlocked.Exchange(ref _changed, NewSignal());
            signal.TrySetResult();
            return new BatchOutcome.Accepted(accepted);
        }
        finally { _lock.Release(); }
    }

    // ---- export / import ------------------------------------------------------------------------

    public async Task<StoreExport> ExportAsync(string account, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = Query($"SELECT {Columns}, deleted_revision FROM store_records WHERE account = $a ORDER BY revision", ("$a", account));
            var history = Query($"SELECT {Columns}, NULL FROM store_versions WHERE account = $a ORDER BY revision", ("$a", account));
            return new StoreExport(ActiveEpoch(account), records, history);
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Loads an export into an account that has no records yet, keeping every revision, so the
    /// bases and cursors participants hold stay valid. Returns false when the account is not empty.
    /// </summary>
    public async Task<bool> ImportAsync(string account, StoreExport export, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using (var count = Command("SELECT COUNT(*) FROM store_records WHERE account = $a", ("$a", account)))
            {
                if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return false;
            }

            using var tx = _connection.BeginTransaction();
            foreach (var r in export.History) Insert(tx, "store_versions", account, r, null);
            foreach (var r in export.Records) Insert(tx, "store_records", account, r, r.DeletedRevision);
            var max = export.Records.Select(r => r.Revision).Concat(export.History.Select(r => r.Revision)).DefaultIfEmpty(0).Max();
            Run(tx, "UPDATE store_counter SET revision = MAX(revision, $m)", ("$m", max));
            Run(tx, "INSERT INTO store_epochs (account, active) VALUES ($a, $e) ON CONFLICT (account) DO UPDATE SET active = $e", ("$a", account), ("$e", export.ActiveEpoch));
            tx.Commit();
            var signal = Interlocked.Exchange(ref _changed, NewSignal());
            signal.TrySetResult();
            return true;
        }
        finally { _lock.Release(); }
    }

    // ---- internals ------------------------------------------------------------------------------

    private static BatchOutcome.Refused? Check(StorePrincipal principal, RecordWrite write, long epoch)
    {
        if (string.IsNullOrWhiteSpace(write.Id) || write.Id.Length > MaxIdLength)
            return new(400, "invalid-id", "A record id is 1 to 512 characters.");
        if (string.IsNullOrWhiteSpace(write.WriteId) || write.WriteId.Length > 128)
            return new(400, "invalid-write-id", "A write needs a writeId of at most 128 characters.");
        if (write.BaseRevision < 0)
            return new(400, "invalid-base", "baseRevision is 0 for a new record or the revision the write is based on.");
        if (!RecordKinds.IsKnown(write.Kind))
            return new(400, "unknown-kind", $"Unknown record kind '{write.Kind}'.");
        if (!string.Equals(write.Actor, principal.Actor, StringComparison.Ordinal))
            return new(403, "actor-mismatch", "The envelope's actor is not the authenticated caller.");
        if (!RecordKinds.MayWrite(principal.Kind, write.Kind))
            return new(403, "kind-not-writable", $"A {principal.Kind.ToString().ToLowerInvariant()} may not write {write.Kind} records.");
        if (write.Epoch != epoch)
            return new(409, "epoch-not-active", $"Epoch {write.Epoch} is not the active epoch ({epoch}).");
        if (!write.Deleted && write.Ciphertext is not { Length: > 0 })
            return new(400, "missing-payload", "A write that is not a deletion carries a sealed payload.");
        return null;
    }

    /// <summary>The version an earlier attempt of this write produced, if the store accepted it.</summary>
    private (StoredRecord? Record, BatchOutcome.Refused? Refusal) Replayed(string account, RecordWrite write)
    {
        var earlier = Query($"SELECT {Columns}, NULL FROM store_versions WHERE account = $a AND write_id = $w", ("$a", account), ("$w", write.WriteId));
        if (earlier.Count == 0) return (null, null);
        var version = earlier[0];
        if (version.Envelope != write.Envelope)
            return (null, new BatchOutcome.Refused(409, "write-id-reused", "This writeId was accepted for a different write."));
        var current = Current(account, write.Id);
        return (version with { DeletedRevision = current?.DeletedRevision }, null);
    }

    private StoredRecord Apply(SqliteTransaction tx, string account, RecordWrite write, StoredRecord? current, DateTimeOffset now)
    {
        using var next = Command("UPDATE store_counter SET revision = revision + 1 RETURNING revision");
        next.Transaction = tx;
        var revision = Convert.ToInt64(next.ExecuteScalar(), CultureInfo.InvariantCulture);
        var deletedRevision = write.Deleted ? revision : current?.DeletedRevision;
        var record = new StoredRecord(write.Id, write.Kind, write.Epoch, write.Actor, write.BaseRevision, write.WriteId,
            write.Deleted, write.Ciphertext, revision, now, deletedRevision);

        Run(tx, "DELETE FROM store_records WHERE account = $a AND id = $id", ("$a", account), ("$id", write.Id));
        Insert(tx, "store_records", account, record, deletedRevision);
        Insert(tx, "store_versions", account, record, null);

        // History: a version stays while it is among the newest 20 or younger than 90 days.
        Run(tx, """
            DELETE FROM store_versions WHERE account = $a AND id = $id AND stored_at < $cutoff AND revision NOT IN (
                SELECT revision FROM store_versions WHERE account = $a AND id = $id ORDER BY revision DESC LIMIT $keep)
            """,
            ("$a", account), ("$id", write.Id), ("$cutoff", Stamp(now - HistoryAge)), ("$keep", HistoryVersions));
        return record;
    }

    private void Insert(SqliteTransaction tx, string table, string account, StoredRecord r, long? deletedRevision)
    {
        var extra = table == "store_records" ? ", deleted_revision" : "";
        var extraValue = table == "store_records" ? ", $dr" : "";
        Run(tx, $"INSERT INTO {table} (account, {Columns}{extra}) VALUES ($a, $id, $kind, $epoch, $actor, $base, $w, $del, $ct, $rev, $at{extraValue})",
            ("$a", account), ("$id", r.Id), ("$kind", r.Kind), ("$epoch", r.Epoch), ("$actor", r.Actor), ("$base", r.BaseRevision),
            ("$w", r.WriteId), ("$del", r.Deleted ? 1 : 0), ("$ct", r.Ciphertext), ("$rev", r.Revision), ("$at", Stamp(r.StoredAt)),
            ("$dr", deletedRevision));
    }

    private StoredRecord? Current(string account, string id) =>
        Query($"SELECT {Columns}, deleted_revision FROM store_records WHERE account = $a AND id = $id", ("$a", account), ("$id", id))
            .FirstOrDefault();

    private long ActiveEpoch(string account)
    {
        using var command = Command("SELECT active FROM store_epochs WHERE account = $a", ("$a", account));
        return command.ExecuteScalar() is { } value and not DBNull ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : 1;
    }

    private List<StoredRecord> Query(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var result = new List<StoredRecord>();
        while (reader.Read())
        {
            result.Add(new StoredRecord(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetInt64(4),
                reader.GetString(5), reader.GetInt64(6) != 0, reader.IsDBNull(7) ? null : (byte[])reader.GetValue(7),
                reader.GetInt64(8), DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
                reader.IsDBNull(10) ? null : reader.GetInt64(10)));
        }

        return result;
    }

    private void Run(SqliteTransaction tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.Transaction = tx;
        command.ExecuteNonQuery();
    }

    private void Execute(string sql)
    {
        using var command = Command(sql);
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
