using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Sqlite;

/// <summary>Durable, append-only audit trail — the enterprise-readiness table stake of plan §4.4.</summary>
public sealed class SqliteAuditLog(SqliteDatabase database) : IAuditLog
{
    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit (at, actor, action, target, outcome, detail)
            VALUES (@at, @actor, @action, @target, @outcome, @detail);
            """;
        command
            .With("@at", SqliteDatabase.Text(entry.At))
            .With("@actor", entry.Actor)
            .With("@action", entry.Action)
            .With("@target", entry.Target)
            .With("@outcome", entry.Outcome.ToString())
            .With("@detail", entry.Detail);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEntry>> QueryAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM audit ORDER BY id DESC LIMIT @limit;";
        command.With("@limit", Math.Max(0, limit));

        var entries = new List<AuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new AuditEntry(
                SqliteDatabase.ReadTime(reader.GetString("at")),
                reader.GetString("actor"),
                reader.GetString("action"),
                reader.GetString("target"),
                SqliteDatabase.ReadEnum<AuditOutcome>(reader.GetString("outcome")),
                reader.GetStringOrNull("detail")));
        }

        return entries;
    }
}
