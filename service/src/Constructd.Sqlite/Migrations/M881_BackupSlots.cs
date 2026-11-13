using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;

// The backups guardrail (docs/plans/construct-platform-design.md 10.2): who asked for a backup (origin: host or
// slot; rows from before have none and count as host-requested), the file count the guest reports, the trigger
// of the host's open request and when the VM last opened a slot session.
public sealed class M881_BackupSlots : ISqliteMigration
{
    public int Id => 881;
    public string Name => "backup-slots";
    public bool Breaking => false;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE backups ADD COLUMN origin TEXT;
            ALTER TABLE backups ADD COLUMN file_count INTEGER;
            ALTER TABLE backup_vms ADD COLUMN request_trigger TEXT;
            ALTER TABLE backup_vms ADD COLUMN slot_last_at TEXT;
            """;
        command.ExecuteNonQuery();
    }
}
