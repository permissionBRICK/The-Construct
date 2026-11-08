using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;

// Host-stored config backups (docs/plans/host-config-backups.md §3.6). Keyed by owner and VM name, with no
// foreign key to vms: backups outlive the VM so a reinstalled VM of the same owner and name finds them.
// The payloads are files under Constructd:Backups:RootDir; the keys files live here.
public sealed class M870_ConfigBackups : ISqliteMigration
{
    public int Id => 870;
    public string Name => "config-backups";
    public bool Breaking => false;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE backups (
                id TEXT PRIMARY KEY, owner TEXT NOT NULL COLLATE NOCASE, vm TEXT NOT NULL COLLATE NOCASE,
                trigger TEXT NOT NULL, state TEXT NOT NULL,
                created_at TEXT NOT NULL, completed_at TEXT, size_bytes INTEGER, sha256 TEXT,
                pinned_until TEXT, keys_json TEXT
            );
            CREATE INDEX ix_backups_owner_vm ON backups(owner, vm);
            CREATE TABLE backup_vms (
                owner TEXT NOT NULL COLLATE NOCASE, vm TEXT NOT NULL COLLATE NOCASE,
                interval_hours INTEGER, keep INTEGER,
                last_attempt_at TEXT, last_success_at TEXT, last_failure_json TEXT, failing_since TEXT,
                request_pending_at TEXT, vm_missing_since TEXT,
                PRIMARY KEY(owner, vm)
            );
            """;
        command.ExecuteNonQuery();
    }
}
