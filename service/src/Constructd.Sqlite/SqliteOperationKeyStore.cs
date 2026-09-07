using System.Globalization;
using Constructd.Core.Abstractions;
using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite;

public sealed class SqliteOperationKeyStore(SqliteDatabase database) : IOperationKeyStore
{
    public async Task<IReadOnlyList<OperationKeyRecord>> ListInFlightAsync(string vmName, CancellationToken ct)
    {
        using var connection = await database.OpenAsync(ct); using var command = connection.CreateCommand();
        command.CommandText = "SELECT owner,kind,key FROM job_operation_keys WHERE state='inFlight' AND target=@vm COLLATE NOCASE";
        command.With("@vm", vmName); var ids = new List<(string Owner, string Kind, string Key)>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return ids.Select(id => Read(connection, null, id.Owner, id.Kind, id.Key)).OfType<OperationKeyRecord>().ToArray();
    }
    public async Task<OperationKeyRecord?> GetAsync(string owner, string kind, string key, CancellationToken ct)
    {
        using var connection = await database.OpenAsync(ct);
        return Read(connection, null, owner, kind, key);
    }
    public async Task<(OperationKeyOutcome Outcome, OperationKeyRecord? Existing)> TryInsertAsync(OperationKeyRecord record, CancellationToken ct)
    {
        using var connection = await database.OpenAsync(ct); using var transaction = connection.BeginTransaction(deferred: false);
        var result = InsertInTransaction(connection, transaction, record); transaction.Commit(); return result;
    }
    public static (OperationKeyOutcome Outcome, OperationKeyRecord? Existing) InsertInTransaction(SqliteConnection connection, SqliteTransaction transaction, OperationKeyRecord record)
    {
        var existing = Read(connection, transaction, record.Owner, record.Kind, record.Key);
        if (existing is not null) return (existing.Fingerprint == record.Fingerprint && string.Equals(existing.Target, record.Target, StringComparison.OrdinalIgnoreCase) ? OperationKeyOutcome.Replay : OperationKeyOutcome.Conflict, existing);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO job_operation_keys VALUES ($owner,$kind,$key,$fingerprint,$target,$job,$state,$intent,$generation,$response,$created,$completed)";
        Bind(command, record.Owner, record.Kind, record.Key);
        command.Parameters.AddWithValue("$fingerprint", record.Fingerprint); command.Parameters.AddWithValue("$target", record.Target);
        command.Parameters.AddWithValue("$job", (object?)record.JobId ?? DBNull.Value); command.Parameters.AddWithValue("$state", record.State == OperationKeyState.InFlight ? "inFlight" : "completed");
        command.Parameters.AddWithValue("$intent", (object?)record.IntentJson ?? DBNull.Value); command.Parameters.AddWithValue("$generation", (object?)record.PowerGeneration ?? DBNull.Value);
        command.Parameters.AddWithValue("$response", (object?)record.ResponseJson ?? DBNull.Value); command.Parameters.AddWithValue("$created", record.Created.ToString("O"));
        command.Parameters.AddWithValue("$completed", record.State == OperationKeyState.Completed ? record.Created.ToString("O") : DBNull.Value);
        command.ExecuteNonQuery(); return (OperationKeyOutcome.Inserted, null);
    }
    public async Task<bool> CompleteAsync(string owner, string kind, string key, string responseJson, CancellationToken ct)
    {
        using var connection = await database.OpenAsync(ct); using var transaction = connection.BeginTransaction(deferred: false);
        var completed = CompleteInTransaction(connection, transaction, owner, kind, key, responseJson); transaction.Commit(); return completed;
    }
    public static bool CompleteInTransaction(SqliteConnection connection, SqliteTransaction transaction, string owner, string kind, string key, string responseJson, DateTimeOffset? completedAt = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; Bind(command, owner, kind, key);
        command.CommandText = "UPDATE job_operation_keys SET state='completed', response_json=$response, completed_at=$completed WHERE owner=$owner AND kind=$kind AND key=$key AND state='inFlight'";
        command.Parameters.AddWithValue("$completed", (completedAt ?? DateTimeOffset.UtcNow).ToString("O"));
        command.Parameters.AddWithValue("$response", responseJson); return command.ExecuteNonQuery() == 1;
    }
    public async Task<bool> RemoveAsync(string owner, string kind, string key, CancellationToken ct)
    {
        using var connection = await database.OpenAsync(ct); using var command = connection.CreateCommand(); Bind(command, owner, kind, key);
        command.CommandText = "DELETE FROM job_operation_keys WHERE owner=$owner AND kind=$kind AND key=$key"; return command.ExecuteNonQuery() == 1;
    }
    public async Task<int> SweepAsync(DateTimeOffset olderThan, CancellationToken ct)
    {
        using var connection = await database.OpenAsync(ct); using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM job_operation_keys WHERE state='completed' AND completed_at<$before AND (job_id IS NULL OR EXISTS(SELECT 1 FROM jobs WHERE jobs.id=job_id AND jobs.state IN ('Succeeded','Failed','Cancelled')))";
        command.Parameters.AddWithValue("$before", olderThan.ToString("O")); return command.ExecuteNonQuery();
    }
    private static void Bind(SqliteCommand command, string owner, string kind, string key)
    { command.Parameters.AddWithValue("$owner", owner); command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$key", key); }
    private static OperationKeyRecord? Read(SqliteConnection connection, SqliteTransaction? transaction, string owner, string kind, string key)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; Bind(command, owner, kind, key);
        command.CommandText = "SELECT * FROM job_operation_keys WHERE owner=$owner AND kind=$kind AND key=$key";
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        string? Nullable(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), Nullable(5), reader.GetString(6) == "inFlight" ? OperationKeyState.InFlight : OperationKeyState.Completed, Nullable(7), reader.IsDBNull(8) ? null : reader.GetInt64(8), Nullable(9), DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture));
    }
}
