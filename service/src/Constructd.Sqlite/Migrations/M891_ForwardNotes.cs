using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;

// Forward notes (docs/expose.md): what a forward is for and where its service lives. NULL on every existing row,
// which keeps working as a forward without a note; an older build ignores both columns.
public sealed class M891_ForwardNotes : ISqliteMigration
{
    public int Id => 891;
    public string Name => "forward-notes";
    public bool Breaking => false;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        SqliteMigrationRunner.AddColumn(connection, transaction, "forwards", "purpose", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "forwards", "source", "TEXT NULL");
    }
}
