using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;

public sealed class M300_CapacityLedger : ISqliteMigration
{
    public int Id => 300;
    public string Name => "capacity-ledger";
    public bool Breaking => false;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE reservations (
                id TEXT PRIMARY KEY,
                resource TEXT NOT NULL CHECK(resource IN ('ram','cpu','storage')),
                scope_owner TEXT NULL COLLATE NOCASE,
                vm_name TEXT NULL COLLATE NOCASE,
                artifact TEXT NULL COLLATE NOCASE,
                volume TEXT NULL COLLATE NOCASE,
                amount INTEGER NOT NULL CHECK(amount >= 0),
                phase TEXT NOT NULL CHECK(phase IN ('pending','held')),
                origin TEXT NOT NULL CHECK(origin IN ('api','external','reconcile')),
                operation_id TEXT NULL,
                pending_until TEXT NULL,
                created TEXT NOT NULL,
                confirmed_at TEXT NULL
            );
            CREATE INDEX ix_reservations_owner ON reservations(scope_owner,resource);
            CREATE INDEX ix_reservations_vm ON reservations(vm_name);
            CREATE INDEX ix_reservations_operation ON reservations(operation_id);
            """;
        command.ExecuteNonQuery();
    }
}
