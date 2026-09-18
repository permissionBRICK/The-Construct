using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;
public sealed class M840_WindowsGuests : ISqliteMigration
{
    public int Id => 840;
    public string Name => "windows-guests";
    public bool Breaking => false;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        SqliteMigrationRunner.AddColumn(connection, transaction, "media", "shared", "INTEGER NOT NULL DEFAULT 0");
        SqliteMigrationRunner.AddColumn(connection, transaction, "media", "windows_json", "TEXT NULL");
    }
}
