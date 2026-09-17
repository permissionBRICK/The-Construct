using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;

public sealed class M810_NestedPolicy : ISqliteMigration
{
    public int Id => 810;
    public string Name => "nested-policy";
    public bool Breaking => false;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "ALTER TABLE users ADD COLUMN allow_nested INTEGER NULL;";
        command.ExecuteNonQuery();
    }
}
