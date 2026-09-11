using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite;

public sealed class SqliteSourceStore(SqliteDatabase database) : ISourceStore
{
    private static SourceItem Read(SqliteDataReader r) => new(r.GetString("commit_sha"),
        SqliteDatabase.ReadEnum<SourceState>(r.GetString("state")), r.GetInt64(r.GetOrdinal("size_bytes")),
        r.GetString("sha256"), r.GetString("release_tag"), r.GetStringOrNull("error"), r.GetStringOrNull("job_id"),
        SqliteDatabase.ReadTime(r.GetString("created")), SqliteDatabase.ReadTimeOrNull(r["ready_at"]),
        SqliteDatabase.ReadTime(r.GetString("last_used_at")));
    private static void Bind(SqliteCommand cmd, SourceItem i) => cmd.With("@commit", i.Commit)
        .With("@state", i.State.ToString().ToLowerInvariant()).With("@size", i.SizeBytes).With("@hash", i.Sha256)
        .With("@tag", i.ReleaseTag).With("@error", i.Error).With("@job", i.JobId)
        .With("@created", SqliteDatabase.Text(i.Created)).With("@ready", SqliteDatabase.TextOrNull(i.ReadyAt))
        .With("@used", SqliteDatabase.Text(i.LastUsedAt));
    public Task<SourceItem?> GetAsync(string commit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM source_cache WHERE commit_sha=@commit"; cmd.With("@commit", commit);
        using var r = cmd.ExecuteReader(); return Task.FromResult(r.Read() ? Read(r) : null);
    }
    public Task<IReadOnlyList<SourceItem>> ListAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM source_cache ORDER BY commit_sha";
        using var r = cmd.ExecuteReader(); var items = new List<SourceItem>(); while (r.Read()) items.Add(Read(r));
        return Task.FromResult<IReadOnlyList<SourceItem>>(items);
    }
    public Task UpsertAsync(SourceItem item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO source_cache (commit_sha,state,size_bytes,sha256,release_tag,error,job_id,created,ready_at,last_used_at)
            VALUES (@commit,@state,@size,@hash,@tag,@error,@job,@created,@ready,@used)
            ON CONFLICT(commit_sha) DO UPDATE SET state=@state,size_bytes=@size,sha256=@hash,release_tag=@tag,
            error=@error,job_id=@job,created=@created,ready_at=@ready,last_used_at=@used
            """;
        Bind(cmd, item); cmd.ExecuteNonQuery(); return Task.CompletedTask;
    }
    public Task<bool> TryTransitionAsync(string commit, SourceState expected, SourceItem updated, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); if (commit != updated.Commit) return Task.FromResult(false);
        using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE source_cache SET state=@state,size_bytes=@size,sha256=@hash,release_tag=@tag,error=@error,
            job_id=@job,created=@created,ready_at=@ready,last_used_at=@used WHERE commit_sha=@commit AND state=@expected
            """;
        Bind(cmd, updated); cmd.With("@expected", expected.ToString().ToLowerInvariant());
        return Task.FromResult(cmd.ExecuteNonQuery() == 1);
    }
    public Task TouchAsync(string commit, DateTimeOffset at, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE source_cache SET last_used_at=@at WHERE commit_sha=@commit";
        cmd.With("@commit", commit).With("@at", SqliteDatabase.Text(at)); cmd.ExecuteNonQuery(); return Task.CompletedTask;
    }
    public Task DeleteAsync(string commit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM source_cache WHERE commit_sha=@commit";
        cmd.With("@commit", commit); cmd.ExecuteNonQuery(); return Task.CompletedTask;
    }
    public Task<long> CommittedBytesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(size_bytes),0) FROM source_cache WHERE state IN ('downloading','ready','deleting')";
        return Task.FromResult(Convert.ToInt64(cmd.ExecuteScalar()));
    }
    public Task<IReadOnlyList<string>> ListPinnedCommitsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT source_commit FROM vms WHERE source_commit IS NOT NULL UNION SELECT guest_construct_commit FROM vms WHERE guest_construct_commit IS NOT NULL";
        using var r = cmd.ExecuteReader(); var pins = new List<string>(); while (r.Read()) pins.Add(r.GetString(0));
        return Task.FromResult<IReadOnlyList<string>>(pins);
    }
}
