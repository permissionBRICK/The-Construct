using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;

// Auto-approved vault entries: VMs read them without the user's approval and are never scrubbed of them.
public sealed class M851_VaultAutoApprove : ISqliteMigration
{
    public int Id => 851;
    public string Name => "vault-auto-approve";
    public bool Breaking => false;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction) =>
        SqliteMigrationRunner.AddColumn(connection, transaction, "vault_entries", "auto_approve", "INTEGER NOT NULL DEFAULT 0");
}
