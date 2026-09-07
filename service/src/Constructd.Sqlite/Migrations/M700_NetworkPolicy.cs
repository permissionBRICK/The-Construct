using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;

public sealed class M700_NetworkPolicy : ISqliteMigration
{
    public int Id => 700;
    public string Name => "network-policy";
    public bool Breaking => false;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = """
            ALTER TABLE forwards ADD COLUMN destination_vm TEXT NULL COLLATE NOCASE;
            ALTER TABLE forwards ADD COLUMN destination_via TEXT NULL COLLATE NOCASE;
            ALTER TABLE forwards ADD COLUMN destination_connect_address TEXT NULL;
            ALTER TABLE forwards ADD COLUMN destination_connect_port INTEGER NULL;
            ALTER TABLE forwards ADD COLUMN requested_by TEXT NULL;
            ALTER TABLE forwards ADD COLUMN relationship TEXT NULL;
            ALTER TABLE forwards ADD COLUMN destination_verified INTEGER NULL;
            CREATE TABLE network_rules (id TEXT PRIMARY KEY, vm_name TEXT NOT NULL COLLATE NOCASE,
                peer TEXT NOT NULL COLLATE NOCASE, kind TEXT NOT NULL, state TEXT NOT NULL,
                created TEXT NOT NULL, updated TEXT NOT NULL);
            CREATE TABLE network_address_history (vm_name TEXT NOT NULL COLLATE NOCASE,
                address TEXT NOT NULL, PRIMARY KEY(vm_name, address));
            """;
        cmd.ExecuteNonQuery();
    }
}
