using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite;

public sealed class SqliteHostUpdateStore(SqliteDatabase database) : IHostUpdateStore, IHostUpdateAdmission
{
    private const string Active = "state IN ('checking','staged','draining','handedOff','applying','recoveryFailed','interrupted')";
    public async Task<HostUpdateRecord?> GetAsync(string id, CancellationToken ct) => (await ReadAsync("id=@id", 1, id, ct)).FirstOrDefault();
    public async Task<HostUpdateRecord?> GetActiveAsync(CancellationToken ct) => (await ReadAsync(Active, 1, null, ct)).FirstOrDefault();
    public Task<IReadOnlyList<HostUpdateRecord>> ListAsync(int limit, CancellationToken ct) => ReadAsync("1=1", Math.Clamp(limit, 0, 100), null, ct);
    private async Task<IReadOnlyList<HostUpdateRecord>> ReadAsync(string where, int limit, string? id, CancellationToken ct)
    {
        await using var c = await database.OpenAsync(ct); await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT * FROM host_updates WHERE {where} ORDER BY started DESC LIMIT @limit";
        cmd.With("@limit", limit); if (id is not null) cmd.With("@id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct); var rows = new List<HostUpdateRecord>();
        while (await r.ReadAsync(ct)) rows.Add(new(r.GetString("id"), r.GetString("commit"), r.GetStringOrNull("release_tag"),
            r.GetStringOrNull("package_version"), Enum.Parse<HostUpdateState>(r.GetString("state"), true), r.GetStringOrNull("phase"),
            WireJson.Read<HostUpdatePhase[]>(r.GetString("phases_json"))!, r.GetTime("started")!.Value, r.GetTime("finished"),
            r.GetStringOrNull("error"), r.GetStringOrNull("previous_commit"), r.GetString("actor"), WireJson.Read<string[]>(r.GetString("blocking_json"))!));
        return rows;
    }
    public async Task<bool> TryStartAsync(HostUpdateRecord record, CancellationToken ct)
    {
        try { await WriteAsync(record, false, ct); return true; }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return false; }
    }
    public Task UpsertAsync(HostUpdateRecord record, CancellationToken ct) => WriteAsync(record, true, ct);
    public async Task<bool> TryAcceptAsync(HostUpdateRecord row, Job queued, OperationKeyRecord? operation, bool fresh, CancellationToken ct)
    {
        try { await WriteAsync(row,!fresh,ct,queued,operation);return true; }
        catch(SqliteException ex) when(ex.SqliteErrorCode==19) {return false;}
    }
    private async Task WriteAsync(HostUpdateRecord r, bool upsert, CancellationToken ct, Job? queued=null, OperationKeyRecord? operation=null)
    {
        await using var c = await database.OpenAsync(ct); await using var tx = c.BeginTransaction(deferred: false);
        if(queued is not null && upsert)
        {
            await using var check=c.CreateCommand();check.Transaction=tx;
            check.CommandText="SELECT state FROM host_updates WHERE id=@id";check.With("@id",r.Id);
            if((string?)await check.ExecuteScalarAsync(ct) is not ("staged" or "interrupted" or "resolvedByAdmin")) throw new SqliteException("Update is not staged.",19);
        }
        await using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO host_updates(id,"commit",release_tag,package_version,state,phase,phases_json,started,finished,error,previous_commit,actor,blocking_json)
            VALUES(@id,@commit,@tag,@version,@state,@phase,@phases,@started,@finished,@error,@previous,@actor,@blocking)
            """ + (upsert ? """
             ON CONFLICT(id) DO UPDATE SET "commit"=excluded."commit", release_tag=excluded.release_tag,
             package_version=excluded.package_version,state=excluded.state,phase=excluded.phase,phases_json=excluded.phases_json,
             finished=excluded.finished,error=excluded.error,blocking_json=excluded.blocking_json
            """ : $" ON CONFLICT DO NOTHING");
        cmd.With("@id",r.Id);cmd.With("@commit",r.Commit);cmd.With("@tag",r.ReleaseTag);cmd.With("@version",r.PackageVersion);
        cmd.With("@state",System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(r.State.ToString()));cmd.With("@phase",r.Phase);
        cmd.With("@phases",WireJson.Serialize(r.Phases));cmd.With("@started",r.Started.ToString("O"));cmd.With("@finished", r.Finished is null ? null : r.Finished.Value.ToString("O"));
        cmd.With("@error",r.Error);cmd.With("@previous",r.PreviousCommit);cmd.With("@actor",r.Actor);cmd.With("@blocking",WireJson.Serialize(r.BlockingJobs));
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new SqliteException("An update is already active.", 19);
        if(queued is not null)
        {
            await using var job=c.CreateCommand();job.Transaction=tx;
            job.CommandText="INSERT INTO jobs(id,kind,vm_name,owner,state,progress,result,error,created,finished,initiator,operation_key,phase) VALUES(@id,'host-update',NULL,@owner,'Queued','[]',NULL,NULL,@created,NULL,@owner,@key,@phase)";
            job.With("@id",queued.Id);job.With("@owner",queued.Owner);job.With("@created",queued.Created.ToString("O"));job.With("@key",queued.OperationKey);job.With("@phase",queued.Phase);
            await job.ExecuteNonQueryAsync(ct);
        }
        if(operation is not null && SqliteOperationKeyStore.InsertInTransaction(c,tx,operation).Outcome!=OperationKeyOutcome.Inserted)
            throw new SqliteException("Operation key conflict.",19);
        await tx.CommitAsync(ct);
    }
}
