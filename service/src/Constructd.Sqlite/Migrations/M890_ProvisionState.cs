using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;

// Provisioning from the engine (design 7.3, 7.5): per VM the last desired state a provision request
// carried and what the guest reported after the last run. Both as JSON; no secret is ever in either.
// No foreign key to vms: a reinstalled VM of the same name finds its settings again.
public sealed class M890_ProvisionState : ISqliteMigration
{
    public int Id => 890;
    public string Name => "provision-state";
    public bool Breaking => false;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE vm_provision_state (
                vm TEXT PRIMARY KEY COLLATE NOCASE,
                desired_json TEXT,
                applied_json TEXT
            );
            """;
        command.ExecuteNonQuery();
    }
}
