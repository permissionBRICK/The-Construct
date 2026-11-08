using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite;

public sealed class SqliteBackupStore(SqliteDatabase database) : IBackupStore
{
    public async Task<ConfigBackup?> GetAsync(string id, CancellationToken ct) =>
        (await QueryAsync("SELECT * FROM backups WHERE id=@id", c => c.With("@id", id), ct)).FirstOrDefault();

    public Task<IReadOnlyList<ConfigBackup>> ListAsync(string owner, string vm, CancellationToken ct) =>
        QueryAsync("SELECT * FROM backups WHERE owner=@owner AND vm=@vm ORDER BY created_at DESC, id DESC",
            c => c.With("@owner", owner).With("@vm", vm), ct);

    public Task<IReadOnlyList<ConfigBackup>> ListAllAsync(CancellationToken ct) =>
        QueryAsync("SELECT * FROM backups ORDER BY created_at DESC, id DESC", _ => { }, ct);

    public Task InsertAsync(ConfigBackup backup, CancellationToken ct) => ExecuteAsync("""
        INSERT INTO backups(id,owner,vm,trigger,state,created_at,completed_at,size_bytes,sha256,pinned_until,keys_json)
        VALUES(@id,@owner,@vm,@trigger,@state,@created,@completed,@size,@sha,@pinned,@keys)
        """, c => c.With("@id", backup.Id).With("@owner", backup.Owner).With("@vm", backup.Vm).With("@trigger", backup.Trigger)
            .With("@state", backup.State).With("@created", SqliteDatabase.Text(backup.CreatedAt))
            .With("@completed", SqliteDatabase.TextOrNull(backup.CompletedAt)).With("@size", backup.SizeBytes).With("@sha", backup.Sha256)
            .With("@pinned", SqliteDatabase.TextOrNull(backup.PinnedUntil)).With("@keys", backup.KeysJson), ct);

    public async Task<bool> SetKeysAsync(string id, string keysJson, CancellationToken ct) =>
        await ExecuteAsync("UPDATE backups SET keys_json=@keys WHERE id=@id AND state='open'",
            c => c.With("@id", id).With("@keys", keysJson), ct) == 1;

    public async Task<bool> CompleteAsync(string id, DateTimeOffset completedAt, long sizeBytes, string sha256, DateTimeOffset? pinnedUntil, CancellationToken ct) =>
        await ExecuteAsync("""
            UPDATE backups SET state='complete', completed_at=@completed, size_bytes=@size, sha256=@sha, pinned_until=@pinned
            WHERE id=@id AND state='open'
            """, c => c.With("@id", id).With("@completed", SqliteDatabase.Text(completedAt)).With("@size", sizeBytes)
                .With("@sha", sha256).With("@pinned", SqliteDatabase.TextOrNull(pinnedUntil)), ct) == 1;

    public async Task<bool> UnpinAsync(string id, CancellationToken ct) =>
        await ExecuteAsync("UPDATE backups SET pinned_until=NULL WHERE id=@id AND state='complete'", c => c.With("@id", id), ct) == 1;

    public async Task<bool> DeleteAsync(string id, CancellationToken ct) =>
        await ExecuteAsync("DELETE FROM backups WHERE id=@id", c => c.With("@id", id), ct) == 1;

    public async Task<BackupVmState?> GetVmAsync(string owner, string vm, CancellationToken ct) =>
        (await QueryVmsAsync("SELECT * FROM backup_vms WHERE owner=@owner AND vm=@vm", c => c.With("@owner", owner).With("@vm", vm), ct)).FirstOrDefault();

    public Task<IReadOnlyList<BackupVmState>> ListVmsAsync(CancellationToken ct) => QueryVmsAsync("SELECT * FROM backup_vms", _ => { }, ct);

    public Task SaveVmAsync(BackupVmState state, CancellationToken ct) => ExecuteAsync("""
        INSERT INTO backup_vms(owner,vm,interval_hours,keep,last_attempt_at,last_success_at,last_failure_json,failing_since,request_pending_at,vm_missing_since)
        VALUES(@owner,@vm,@interval,@keep,@attempt,@success,@failure,@failing,@request,@missing)
        ON CONFLICT(owner,vm) DO UPDATE SET interval_hours=excluded.interval_hours, keep=excluded.keep,
            last_attempt_at=excluded.last_attempt_at, last_success_at=excluded.last_success_at,
            last_failure_json=excluded.last_failure_json, failing_since=excluded.failing_since,
            request_pending_at=excluded.request_pending_at, vm_missing_since=excluded.vm_missing_since
        """, c => c.With("@owner", state.Owner).With("@vm", state.Vm).With("@interval", state.IntervalHours).With("@keep", state.Keep)
            .With("@attempt", SqliteDatabase.TextOrNull(state.LastAttemptAt)).With("@success", SqliteDatabase.TextOrNull(state.LastSuccessAt))
            .With("@failure", WireJson.Serialize(state.LastFailure)).With("@failing", SqliteDatabase.TextOrNull(state.FailingSince))
            .With("@request", SqliteDatabase.TextOrNull(state.RequestPendingAt)).With("@missing", SqliteDatabase.TextOrNull(state.VmMissingSince)), ct);

    public Task DeleteVmAsync(string owner, string vm, CancellationToken ct) =>
        ExecuteAsync("DELETE FROM backup_vms WHERE owner=@owner AND vm=@vm", c => c.With("@owner", owner).With("@vm", vm), ct);

    private async Task<int> ExecuteAsync(string sql, Action<SqliteCommand> bind, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = sql; bind(command);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyList<ConfigBackup>> QueryAsync(string sql, Action<SqliteCommand> bind, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = sql; bind(command);
        using var r = await command.ExecuteReaderAsync(ct);
        var rows = new List<ConfigBackup>();
        while (await r.ReadAsync(ct))
            rows.Add(new(r.GetString("id"), r.GetString("owner"), r.GetString("vm"), r.GetString("trigger"), r.GetString("state"),
                SqliteDatabase.ReadTime(r.GetString("created_at")), r.GetTime("completed_at"), r.GetLongOrNull("size_bytes"),
                r.GetStringOrNull("sha256"), r.GetTime("pinned_until"), r.GetStringOrNull("keys_json")));
        return rows;
    }

    private async Task<IReadOnlyList<BackupVmState>> QueryVmsAsync(string sql, Action<SqliteCommand> bind, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = sql; bind(command);
        using var r = await command.ExecuteReaderAsync(ct);
        var rows = new List<BackupVmState>();
        while (await r.ReadAsync(ct))
            rows.Add(new(r.GetString("owner"), r.GetString("vm"), r.GetIntOrNull("interval_hours"), r.GetIntOrNull("keep"),
                r.GetTime("last_attempt_at"), r.GetTime("last_success_at"), WireJson.Read<BackupFailure>(r.GetStringOrNull("last_failure_json")),
                r.GetTime("failing_since"), r.GetTime("request_pending_at"), r.GetTime("vm_missing_since")));
        return rows;
    }
}
