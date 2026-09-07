using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;
public sealed class M600_HostUpdates : ISqliteMigration
{
    public int Id => 600;
    public string Name => "host-updates";
    public bool Breaking => false;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = """
            CREATE TABLE host_updates (
              id TEXT PRIMARY KEY, "commit" TEXT NOT NULL, release_tag TEXT, package_version TEXT,
              state TEXT NOT NULL, phase TEXT, phases_json TEXT NOT NULL, started TEXT NOT NULL,
              finished TEXT, error TEXT, previous_commit TEXT, actor TEXT NOT NULL, blocking_json TEXT NOT NULL);
            CREATE UNIQUE INDEX ix_host_updates_active ON host_updates ((1))
              WHERE state IN ('checking','staged','draining','handedOff','applying','recoveryFailed','interrupted');
            """;
        cmd.ExecuteNonQuery();
    }
}
