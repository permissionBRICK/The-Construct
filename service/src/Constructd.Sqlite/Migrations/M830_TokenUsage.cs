using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;

public sealed class M830_TokenUsage : ISqliteMigration
{
    public int Id => 830;
    public string Name => "token-usage";
    public bool Breaking => false;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE token_usage (
                vm_name TEXT NOT NULL COLLATE NOCASE, owner TEXT NOT NULL COLLATE NOCASE,
                tool TEXT NOT NULL, day TEXT NOT NULL,
                input_tokens INTEGER NOT NULL, output_tokens INTEGER NOT NULL,
                cache_create_tokens INTEGER NOT NULL, cache_read_tokens INTEGER NOT NULL,
                total_tokens INTEGER NOT NULL, cost_usd_micros INTEGER NOT NULL,
                models_json TEXT NOT NULL, reported_at TEXT NOT NULL, vm_deleted_at TEXT NULL,
                PRIMARY KEY(vm_name, tool, day)
            );
            CREATE INDEX ix_token_usage_owner ON token_usage(owner);
            CREATE TRIGGER token_usage_vm_deleted AFTER DELETE ON vms BEGIN
                UPDATE token_usage SET vm_deleted_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                WHERE vm_name = OLD.name AND vm_deleted_at IS NULL;
            END;
            """;
        command.ExecuteNonQuery();
    }
}
