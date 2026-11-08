using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;

// HTTPS forward names (docs/https-forwards.md): each HTTPS forward is also served as <vm>-<name> on the shared
// HTTPS port. NULL means the default name, the VM port, so existing rows need no backfill. An older build ignores
// the column and serves such forwards on their own port only, which is the safe fallback for a rollback.
public sealed class M870_HttpsForwardNames : ISqliteMigration
{
    public int Id => 870;
    public string Name => "https-forward-names";
    public bool Breaking => false;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction) =>
        SqliteMigrationRunner.AddColumn(connection, transaction, "forwards", "https_name", "TEXT NULL");
}
