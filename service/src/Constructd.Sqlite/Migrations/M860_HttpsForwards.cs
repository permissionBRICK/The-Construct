using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;

// HTTPS forwards (docs/https-forwards.md): the host terminates TLS on the forward's public port, and one
// forward per VM may be its main web UI on the shared HTTPS port. An older build reads these rows as plain
// host forwards, which is the safe fallback for a rollback.
public sealed class M860_HttpsForwards : ISqliteMigration
{
    public int Id => 860;
    public string Name => "https-forwards";
    public bool Breaking => false;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        SqliteMigrationRunner.AddColumn(connection, transaction, "forwards", "https", "INTEGER NOT NULL DEFAULT 0");
        SqliteMigrationRunner.AddColumn(connection, transaction, "forwards", "main", "INTEGER NOT NULL DEFAULT 0");
        SqliteMigrationRunner.AddColumn(connection, transaction, "forwards", "upstream_tls", "INTEGER NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "users", "allow_https_forwards", "INTEGER NOT NULL DEFAULT 0");
    }
}
