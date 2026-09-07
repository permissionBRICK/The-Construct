using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;

public interface ISqliteMigration
{
    int Id { get; }
    string Name { get; }
    bool Breaking { get; }
    void Apply(SqliteConnection connection, SqliteTransaction transaction);
}
