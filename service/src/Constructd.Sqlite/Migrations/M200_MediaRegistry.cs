using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;
public sealed class M200_MediaRegistry : ISqliteMigration
{
    public int Id => 200;
    public string Name => "media-registry";
    public bool Breaking => false;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE media (
                id TEXT PRIMARY KEY, owner TEXT NOT NULL COLLATE NOCASE, name TEXT NOT NULL,
                role TEXT NOT NULL, source TEXT NOT NULL, source_url TEXT NULL, path TEXT NOT NULL,
                state TEXT NOT NULL, size_bytes INTEGER NULL, reserved_bytes INTEGER NOT NULL,
                sha256 TEXT NULL, expected_sha256 TEXT NULL, error TEXT NULL, job_id TEXT NULL,
                dedicated_to TEXT NULL COLLATE NOCASE, created TEXT NOT NULL, ready_at TEXT NULL,
                last_referenced_at TEXT NULL, reservation_id TEXT NULL);
            CREATE INDEX ix_media_owner ON media(owner);
            CREATE TABLE media_references (
                media_id TEXT NOT NULL REFERENCES media(id), vm_name TEXT NOT NULL COLLATE NOCASE,
                slot TEXT NOT NULL, created TEXT NOT NULL, PRIMARY KEY(media_id, vm_name, slot));
            CREATE TABLE media_uploads (
                id TEXT PRIMARY KEY, media_id TEXT NOT NULL, owner TEXT NOT NULL COLLATE NOCASE,
                size_bytes INTEGER NOT NULL, chunk_bytes INTEGER NOT NULL, received_json TEXT NOT NULL,
                state TEXT NOT NULL, operation_key TEXT NULL, created TEXT NOT NULL, expires_at TEXT NOT NULL);
            CREATE INDEX ix_media_uploads_expiry ON media_uploads(state, expires_at);
            """;
        command.ExecuteNonQuery();
    }
}
