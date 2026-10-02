using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;

// The hosted key vault (docs/plans/key-vault-hosted.md). Entries hold only sealed payloads; the per-VM
// records keep their JSON body next to the columns they are found by.
public sealed class M850_KeyVault : ISqliteMigration
{
    public int Id => 850;
    public string Name => "key-vault";
    public bool Breaking => false;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE vault_owners (
                owner TEXT PRIMARY KEY COLLATE NOCASE, revision INTEGER NOT NULL,
                mode TEXT NULL, key_check TEXT NULL, wrapped_key TEXT NULL);
            CREATE TABLE vault_entries (
                owner TEXT NOT NULL COLLATE NOCASE, name TEXT NOT NULL, description TEXT NOT NULL,
                has_username INTEGER NOT NULL, payload TEXT NULL, updated_at INTEGER NOT NULL,
                updated_by TEXT NOT NULL, deleted INTEGER NOT NULL, PRIMARY KEY(owner, name));
            CREATE TABLE vault_leases (id TEXT PRIMARY KEY, owner TEXT NOT NULL COLLATE NOCASE, vm TEXT NOT NULL COLLATE NOCASE, body TEXT NOT NULL);
            CREATE INDEX ix_vault_leases_owner ON vault_leases(owner);
            CREATE TABLE vault_cleanups (id TEXT PRIMARY KEY, owner TEXT NOT NULL COLLATE NOCASE, vm TEXT NOT NULL COLLATE NOCASE, body TEXT NOT NULL);
            CREATE INDEX ix_vault_cleanups_owner ON vault_cleanups(owner);
            CREATE TABLE vault_scrubs (id TEXT PRIMARY KEY, owner TEXT NOT NULL COLLATE NOCASE, vm TEXT NOT NULL COLLATE NOCASE, body TEXT NOT NULL);
            CREATE INDEX ix_vault_scrubs_owner ON vault_scrubs(owner);
            CREATE TABLE vault_files (id TEXT PRIMARY KEY, owner TEXT NOT NULL COLLATE NOCASE, vm TEXT NOT NULL COLLATE NOCASE, body TEXT NOT NULL);
            CREATE INDEX ix_vault_files_owner ON vault_files(owner);
            CREATE TABLE vault_devices (
                id TEXT PRIMARY KEY, owner TEXT NOT NULL COLLATE NOCASE, label TEXT NOT NULL,
                token_hash TEXT NOT NULL UNIQUE, created_at TEXT NOT NULL, last_used_at TEXT NULL);
            CREATE INDEX ix_vault_devices_owner ON vault_devices(owner);
            CREATE TABLE vault_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT, owner TEXT NOT NULL COLLATE NOCASE, at TEXT NOT NULL,
                vm TEXT NOT NULL, text TEXT NOT NULL, warning INTEGER NOT NULL);
            CREATE INDEX ix_vault_events_owner ON vault_events(owner, id);
            """;
        command.ExecuteNonQuery();
    }
}
