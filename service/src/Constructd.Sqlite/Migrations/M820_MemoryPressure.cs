using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;

public sealed class M820_MemoryPressure : ISqliteMigration
{
    public int Id => 820;
    public string Name => "memory-pressure";
    public bool Breaking => false;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE vms ADD COLUMN pressure_saved_at TEXT NULL;
            ALTER TABLE vms ADD COLUMN pressure_saved_generation INTEGER NULL;
            """;
        command.ExecuteNonQuery();
    }
}
