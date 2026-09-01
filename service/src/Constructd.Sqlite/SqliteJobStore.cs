using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Microsoft.Data.Sqlite;

namespace Constructd.Sqlite;

/// <summary>
/// Durable job state, so a client can still read what happened to a job after the service restarted.
///
/// Progress and result are stored as JSON. A result read back from the database comes out as a
/// <see cref="JsonElement"/> rather than its original type — the API serializes it either way, and
/// the engine serves jobs of the running process from memory. No secret is ever written: the
/// one-time secret of a job lives only in the engine, so a restart loses it (by design).
/// </summary>
public sealed class SqliteJobStore(SqliteDatabase database) : IJobStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task UpsertAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs (id, kind, vm_name, owner, state, progress, result, error, created, finished)
            VALUES (@id, @kind, @vmName, @owner, @state, @progress, @result, @error, @created, @finished)
            ON CONFLICT(id) DO UPDATE
               SET state = excluded.state, progress = excluded.progress, result = excluded.result,
                   error = excluded.error, finished = excluded.finished;
            """;
        command
            .With("@id", job.Id)
            .With("@kind", job.Kind)
            .With("@vmName", job.VmName)
            .With("@owner", job.Owner)
            .With("@state", job.State.ToString())
            .With("@progress", JsonSerializer.Serialize(job.Progress, Json))
            .With("@result", job.Result is null ? null : JsonSerializer.Serialize(job.Result, Json))
            .With("@error", job.Error)
            .With("@created", SqliteDatabase.Text(job.Created))
            .With("@finished", SqliteDatabase.TextOrNull(job.Finished));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Job?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM jobs WHERE id = @id;";
        command.With("@id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<int> MarkInterruptedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs
               SET state = 'Failed', error = 'interrupted by a service restart', finished = @now
             WHERE state IN ('Queued', 'Running');
            """;
        command.With("@now", SqliteDatabase.Text(now));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Job Read(SqliteDataReader reader)
    {
        var progress = JsonSerializer.Deserialize<List<JobProgressLine>>(reader.GetString("progress"), Json) ?? [];
        var resultJson = reader.GetStringOrNull("result");

        return new Job(
            reader.GetString("id"),
            reader.GetString("kind"),
            reader.GetStringOrNull("vm_name"),
            reader.GetString("owner"),
            SqliteDatabase.ReadEnum<JobState>(reader.GetString("state")),
            progress,
            resultJson is null ? null : JsonSerializer.Deserialize<JsonElement>(resultJson, Json),
            reader.GetStringOrNull("error"),
            SqliteDatabase.ReadTime(reader.GetString("created")),
            SqliteDatabase.ReadTimeOrNull(reader.GetStringOrNull("finished")));
    }
}
