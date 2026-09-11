using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;

public sealed class M800_SourceCache : ISqliteMigration
{
    public int Id => 800;
    public string Name => "source-cache";
    public bool Breaking => false;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS source_cache (
              commit_sha TEXT PRIMARY KEY,
              state TEXT NOT NULL,
              size_bytes INTEGER NOT NULL,
              sha256 TEXT NOT NULL,
              release_tag TEXT NOT NULL,
              error TEXT NULL,
              job_id TEXT NULL,
              created TEXT NOT NULL,
              ready_at TEXT NULL,
              last_used_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_source_cache_last_used ON source_cache(last_used_at);
            ALTER TABLE vms ADD COLUMN source_commit TEXT NULL;
            """;
        cmd.ExecuteNonQuery();
    }
}
