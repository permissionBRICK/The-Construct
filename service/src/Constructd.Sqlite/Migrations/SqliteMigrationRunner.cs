using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;

public static class SqliteMigrationRunner
{
    public static void Apply(SqliteConnection connection, string appCommit,
        IReadOnlyList<ISqliteMigration>? migrations = null)
    {
        var ordered = (migrations ?? SqliteMigrations.All).OrderBy(m => m.Id).ToArray();
        if (ordered.Select(m => m.Id).Distinct().Count() != ordered.Length)
            throw new InvalidOperationException("Duplicate migration id.");
        using var transaction = connection.BeginTransaction(deferred: false);
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                id INTEGER PRIMARY KEY, name TEXT NOT NULL, breaking INTEGER NOT NULL,
                applied_at TEXT NOT NULL, applied_by_commit TEXT NOT NULL);
            """);
        using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "SELECT COALESCE(MAX(id),0), COALESCE(MAX(CASE WHEN breaking=1 THEN id END),0) FROM schema_migrations";
            using var row = version.ExecuteReader(); row.Read();
            if (row.GetInt32(1) > SqliteMigrations.SchemaVersion)
                throw new InvalidOperationException("The database requires a newer constructd binary (breaking schema migration).");
        }
        foreach (var migration in ordered)
        {
            using var probe = connection.CreateCommand();
            probe.Transaction = transaction;
            probe.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE id=@id";
            probe.With("@id", migration.Id);
            if (Convert.ToInt64(probe.ExecuteScalar()) != 0) continue;
            migration.Apply(connection, transaction);
            using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO schema_migrations VALUES (@id,@name,@breaking,@at,@commit)";
            record.With("@id", migration.Id).With("@name", migration.Name)
                .With("@breaking", migration.Breaking ? 1 : 0)
                .With("@at", SqliteDatabase.Text(DateTimeOffset.UtcNow)).With("@commit", appCommit);
            record.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    internal static void CheckCompatibility(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations'";
        if (Convert.ToInt64(command.ExecuteScalar()) == 0) return;
        command.CommandText = "SELECT COALESCE(MAX(id),0) FROM schema_migrations WHERE breaking=1";
        if (Convert.ToInt64(command.ExecuteScalar()) > SqliteMigrations.SchemaVersion)
            throw new InvalidOperationException("The database requires a newer constructd binary (breaking schema migration).");
    }

    internal static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static void AddColumn(SqliteConnection connection, SqliteTransaction transaction,
        string table, string column, string definition)
    {
        using var probe = connection.CreateCommand();
        probe.Transaction = transaction;
        probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name=@column";
        probe.With("@table", table).With("@column", column);
        if (Convert.ToInt64(probe.ExecuteScalar()) == 0)
            Execute(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }
}
