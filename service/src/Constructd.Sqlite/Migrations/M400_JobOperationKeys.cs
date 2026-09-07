using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;

public sealed class M400_JobOperationKeys : ISqliteMigration
{
    public int Id => 400;
    public string Name => "job-operation-keys";
    public bool Breaking => false;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE job_operation_keys (
                owner TEXT NOT NULL COLLATE NOCASE, kind TEXT NOT NULL, key TEXT NOT NULL,
                fingerprint TEXT NOT NULL, target TEXT NOT NULL COLLATE NOCASE, job_id TEXT NULL,
                state TEXT NOT NULL, intent_json TEXT NULL, power_generation INTEGER NULL,
                response_json TEXT NULL, created TEXT NOT NULL, completed_at TEXT NULL,
                PRIMARY KEY(owner, kind, key));
            CREATE INDEX ix_operation_keys_created ON job_operation_keys(created);
            """;
        command.ExecuteNonQuery();
    }
}
