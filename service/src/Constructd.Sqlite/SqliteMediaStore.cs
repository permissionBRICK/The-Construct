using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite;

public sealed class SqliteMediaStore(SqliteDatabase database) : IMediaStore
{
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql)
    { var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; return cmd; }
    private static void Bind(SqliteCommand cmd, MediaItem i)
    {
        cmd.With("$id", i.Id);
        cmd.With("$owner", i.Owner);
        cmd.With("$name", i.Name);
        cmd.With("$role", i.Role.ToString().ToLowerInvariant());
        cmd.With("$source", i.Source.ToString().ToLowerInvariant());
        cmd.With("$source_url", i.SourceUrl);
        cmd.With("$path", i.Path);
        cmd.With("$state", i.State.ToString().ToLowerInvariant());
        cmd.With("$size_bytes", i.SizeBytes);
        cmd.With("$reserved_bytes", i.ReservedBytes);
        cmd.With("$sha256", i.Sha256);
        cmd.With("$expected_sha256", i.ExpectedSha256);
        cmd.With("$error", i.Error);
        cmd.With("$job_id", i.JobId);
        cmd.With("$dedicated_to", i.DedicatedTo);
        cmd.With("$created", SqliteDatabase.Text(i.Created));
        cmd.With("$ready_at", SqliteDatabase.TextOrNull(i.ReadyAt));
        cmd.With("$last_referenced_at", SqliteDatabase.TextOrNull(i.LastReferencedAt));
    }
    private static MediaItem Read(SqliteDataReader r) => new(
        r.GetString("id"),
        r.GetString("owner"),
        r.GetString("name"),
        SqliteDatabase.ReadEnum<MediaRole>(r.GetString("role")),
        SqliteDatabase.ReadEnum<MediaSource>(r.GetString("source")),
        r.GetStringOrNull("source_url"),
        r.GetString("path"),
        SqliteDatabase.ReadEnum<MediaState>(r.GetString("state")),
        r.IsDBNull(r.GetOrdinal("size_bytes")) ? null : r.GetInt64(r.GetOrdinal("size_bytes")),
        r.GetInt64(r.GetOrdinal("reserved_bytes")),
        r.GetStringOrNull("sha256"),
        r.GetStringOrNull("expected_sha256"),
        r.GetStringOrNull("error"),
        r.GetStringOrNull("job_id"),
        r.GetStringOrNull("dedicated_to"),
        SqliteDatabase.ReadTime(r.GetString("created")),
        SqliteDatabase.ReadTimeOrNull(r["ready_at"]),
        SqliteDatabase.ReadTimeOrNull(r["last_referenced_at"]));
    /// <summary>Called by the shared admission transaction, never opens a second connection.</summary>
    public static void InsertInTransaction(SqliteConnection connection, SqliteTransaction transaction, MediaItem item)
    {
        using var cmd = Command(connection, transaction, "INSERT INTO media (id, owner, name, role, source, source_url, path, state, size_bytes, reserved_bytes, sha256, expected_sha256, error, job_id, dedicated_to, created, ready_at, last_referenced_at) VALUES ($id, $owner, $name, $role, $source, $source_url, $path, $state, $size_bytes, $reserved_bytes, $sha256, $expected_sha256, $error, $job_id, $dedicated_to, $created, $ready_at, $last_referenced_at);");
        Bind(cmd, item); cmd.ExecuteNonQuery();
    }
    public Task AddAsync(MediaItem item, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var tx = c.BeginTransaction(deferred: false); InsertInTransaction(c, tx, item); tx.Commit(); return Task.CompletedTask; }
    public Task<MediaItem?> GetAsync(string id, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = Command(c, null, "SELECT * FROM media WHERE id=$id;").With("$id", id); using var r = cmd.ExecuteReader(); return Task.FromResult(r.Read() ? Read(r) : null); }
    public Task<IReadOnlyList<MediaItem>> ListAsync(string? owner, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = Command(c, null, "SELECT * FROM media WHERE $owner IS NULL OR owner=$owner;").With("$owner", owner); using var r = cmd.ExecuteReader(); var list = new List<MediaItem>(); while(r.Read()) list.Add(Read(r)); return Task.FromResult<IReadOnlyList<MediaItem>>(list); }
    public Task<int> CountByOwnerAsync(string owner, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = Command(c, null, "SELECT COUNT(*) FROM media WHERE owner=$owner AND state IN ('pending','transferring','ready');").With("$owner", owner); return Task.FromResult(Convert.ToInt32(cmd.ExecuteScalar())); }
    public Task<bool> TryTransitionAsync(string id, MediaState expected, MediaItem updated, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); if (updated.Id != id) return Task.FromResult(false);
        using var c = database.Open();
        using var cmd = Command(c, null, "UPDATE media SET name=$name, role=$role, source=$source, source_url=$source_url, path=$path, state=$state, size_bytes=$size_bytes, reserved_bytes=$reserved_bytes, sha256=$sha256, expected_sha256=$expected_sha256, error=$error, job_id=$job_id, dedicated_to=$dedicated_to, created=$created, ready_at=$ready_at, last_referenced_at=$last_referenced_at WHERE id=$id AND owner=$owner AND state=$expected AND ($state!='deleting' OR NOT EXISTS (SELECT 1 FROM media_references WHERE media_id=$id));");
        Bind(cmd, updated); cmd.With("$expected", expected.ToString().ToLowerInvariant()); return Task.FromResult(cmd.ExecuteNonQuery() == 1);
    }
    public Task<bool> RemoveAsync(string id, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = Command(c, null, "DELETE FROM media WHERE id=$id AND NOT EXISTS (SELECT 1 FROM media_references WHERE media_id=$id);").With("$id", id); return Task.FromResult(cmd.ExecuteNonQuery() == 1); }
    private Task<IReadOnlyList<MediaReference>> References(string sql, string value, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = Command(c, null, sql).With("$value", value); using var r = cmd.ExecuteReader(); var list = new List<MediaReference>(); while(r.Read()) list.Add(new(r.GetString("media_id"), r.GetString("vm_name"), SqliteDatabase.ReadEnum<MediaSlot>(r.GetString("slot")), SqliteDatabase.ReadTime(r.GetString("created")))); return Task.FromResult<IReadOnlyList<MediaReference>>(list); }
    public Task<IReadOnlyList<MediaReference>> ListReferencesAsync(string mediaId, CancellationToken ct) => References("SELECT * FROM media_references WHERE media_id=$value;", mediaId, ct);
    public Task<IReadOnlyList<MediaReference>> ListReferencesForVmAsync(string vmName, CancellationToken ct) => References("SELECT * FROM media_references WHERE vm_name=$value;", vmName, ct);
    public static bool InsertInTransaction(SqliteConnection c, SqliteTransaction tx, MediaReference reference)
    {
        using var cmd = Command(c, tx, "INSERT INTO media_references SELECT $id,$vm,$slot,$created WHERE EXISTS (SELECT 1 FROM media WHERE id=$id AND state='ready') ON CONFLICT DO NOTHING;")
            .With("$id", reference.MediaId).With("$vm", reference.VmName).With("$slot", reference.Slot.ToString().ToLowerInvariant()).With("$created", SqliteDatabase.Text(reference.Created));
        cmd.ExecuteNonQuery();
        using var touch = Command(c, tx, "UPDATE media SET last_referenced_at=$created WHERE id=$id AND state='ready';").With("$id", reference.MediaId).With("$created", SqliteDatabase.Text(reference.Created));
        return touch.ExecuteNonQuery() == 1;
    }
    public Task<bool> TryAddReferenceAsync(MediaReference reference, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var tx = c.BeginTransaction(deferred: false); var ok = InsertInTransaction(c, tx, reference); tx.Commit(); return Task.FromResult(ok); }
    public Task<bool> RemoveReferenceAsync(string mediaId, string vmName, MediaSlot slot, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = Command(c, null, "DELETE FROM media_references WHERE media_id=$id AND vm_name=$vm AND slot=$slot;").With("$id", mediaId).With("$vm", vmName).With("$slot", slot.ToString().ToLowerInvariant()); return Task.FromResult(cmd.ExecuteNonQuery() == 1); }
    private static MediaUpload ReadUpload(SqliteDataReader r) => new(r.GetString("id"), r.GetString("media_id"), r.GetString("owner"), r.GetInt64(r.GetOrdinal("size_bytes")), r.GetInt("chunk_bytes"), JsonSerializer.Deserialize<int[]>(r.GetString("received_json"))!, SqliteDatabase.ReadEnum<UploadState>(r.GetString("state")), r.GetStringOrNull("operation_key"), SqliteDatabase.ReadTime(r.GetString("created")), SqliteDatabase.ReadTime(r.GetString("expires_at")));
    private static void BindUpload(SqliteCommand cmd, MediaUpload u) => cmd.With("$id",u.Id).With("$media",u.MediaId).With("$owner",u.Owner).With("$size",u.SizeBytes).With("$chunk",u.ChunkBytes).With("$received",JsonSerializer.Serialize(u.Received)).With("$state",u.State.ToString().ToLowerInvariant()).With("$key",u.OperationKey).With("$created",SqliteDatabase.Text(u.Created)).With("$expires",SqliteDatabase.Text(u.ExpiresAt));
    public static void InsertInTransaction(SqliteConnection c, SqliteTransaction tx, MediaUpload upload)
    { using var cmd = Command(c, tx, "INSERT INTO media_uploads VALUES ($id,$media,$owner,$size,$chunk,$received,$state,$key,$created,$expires);"); BindUpload(cmd, upload); cmd.ExecuteNonQuery(); }
    public Task AddUploadAsync(MediaUpload upload, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var tx = c.BeginTransaction(deferred: false); InsertInTransaction(c, tx, upload); tx.Commit(); return Task.CompletedTask; }
    public Task<MediaUpload?> GetUploadAsync(string id, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = Command(c, null, "SELECT * FROM media_uploads WHERE id=$id;").With("$id",id); using var r = cmd.ExecuteReader(); return Task.FromResult(r.Read() ? ReadUpload(r) : null); }
    public Task<bool> TryTransitionUploadAsync(string id, UploadState expected, MediaUpload updated, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); if (updated.Id != id) return Task.FromResult(false);
        using var c = database.Open(); using var cmd = Command(c, null, "UPDATE media_uploads SET state=$state,received_json=$received WHERE id=$id AND owner=$owner AND media_id=$media AND state=$expected;");
        BindUpload(cmd,updated); cmd.With("$expected",expected.ToString().ToLowerInvariant()); return Task.FromResult(cmd.ExecuteNonQuery() == 1);
    }
    public Task<bool> RecordChunkAsync(string uploadId, int index, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var tx = c.BeginTransaction(deferred: false);
        MediaUpload? u;
        using(var cmd = Command(c,tx,"SELECT * FROM media_uploads WHERE id=$id;").With("$id",uploadId))
        using(var r = cmd.ExecuteReader()) u = r.Read() ? ReadUpload(r) : null;
        if(u is null || u.State != UploadState.Open || index < 0 || index >= (u.SizeBytes + u.ChunkBytes - 1) / u.ChunkBytes) return Task.FromResult(false);
        using var write = Command(c,tx,"UPDATE media_uploads SET received_json=$received WHERE id=$id;").With("$id",uploadId).With("$received",JsonSerializer.Serialize(u.Received.Append(index).Distinct().Order()));
        write.ExecuteNonQuery(); tx.Commit(); return Task.FromResult(true);
    }
    public Task<IReadOnlyList<MediaUpload>> ListExpiredUploadsAsync(DateTimeOffset now, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = Command(c,null,"SELECT * FROM media_uploads WHERE state='open' AND expires_at<=$now;").With("$now",SqliteDatabase.Text(now)); using var r = cmd.ExecuteReader(); var list = new List<MediaUpload>(); while(r.Read()) list.Add(ReadUpload(r)); return Task.FromResult<IReadOnlyList<MediaUpload>>(list); }
}
