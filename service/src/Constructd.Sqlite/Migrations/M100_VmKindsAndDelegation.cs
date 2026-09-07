using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite.Migrations;

public sealed class M100_VmKindsAndDelegation : ISqliteMigration
{
    public int Id => 100;
    public string Name => "VM kinds and delegation";
    public bool Breaking => false;
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "power_generation", "INTEGER NOT NULL DEFAULT 0");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "kind", "TEXT NOT NULL DEFAULT 'primary'");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "parent", "TEXT NULL COLLATE NOCASE");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "sharing", "TEXT NOT NULL DEFAULT 'private'");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "vm_token_kind", "TEXT NOT NULL DEFAULT 'legacy'");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "ram_mb", "INTEGER NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "incarnation", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "lease_requested_text", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "lease_requested_seconds", "INTEGER NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "lease_activated_at", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "lease_expires_at", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "lease_state", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "lease_version", "INTEGER NOT NULL DEFAULT 0");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "lease_last_attempt_at", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "lease_last_outcome", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "hardware_json", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "guest_construct_commit", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "guest_provisioned_at", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "guest_reinstalled_at", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "guest_reported_at", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "guest_provenance", "TEXT NULL DEFAULT 'unknown'");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "guest_last_attempt_at", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "guest_last_attempt_outcome", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "observed_created_at", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "observed_last_boot_at", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "observed_addresses_json", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "observed_storage_problem", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "child_creation_closed", "INTEGER NOT NULL DEFAULT 0");
        SqliteMigrationRunner.AddColumn(connection, transaction, "vms", "current_job_id", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "users", "enabled", "INTEGER NOT NULL DEFAULT 1");
        SqliteMigrationRunner.AddColumn(connection, transaction, "users", "allow_child_creation", "INTEGER NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "users", "max_retained_children", "INTEGER NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "users", "cpu_budget", "INTEGER NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "users", "ram_budget_bytes", "INTEGER NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "users", "storage_budget_bytes", "INTEGER NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "users", "max_child_lifetime_seconds", "INTEGER NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "users", "allow_never_lifetime", "INTEGER NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "users", "allow_sharing", "INTEGER NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "jobs", "initiator", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "jobs", "operation_key", "TEXT NULL");
        SqliteMigrationRunner.AddColumn(connection, transaction, "jobs", "phase", "TEXT NULL");
        SqliteMigrationRunner.Execute(connection, transaction, """
            CREATE INDEX IF NOT EXISTS ix_vms_parent ON vms(parent);
            CREATE INDEX IF NOT EXISTS ix_vms_lease_due ON vms(lease_state,lease_expires_at);
            CREATE TABLE IF NOT EXISTS vm_overrides (
                vm_name TEXT PRIMARY KEY COLLATE NOCASE, allow_child_creation INTEGER NULL,
                max_retained_children INTEGER NULL, max_child_lifetime_seconds INTEGER NULL,
                allow_never_lifetime INTEGER NULL, allow_sharing INTEGER NULL, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS cascades (
                parent TEXT PRIMARY KEY COLLATE NOCASE, parent_incarnation TEXT NULL,
                token TEXT NOT NULL, issued_at TEXT NOT NULL, expires_at TEXT NOT NULL,
                children_json TEXT NOT NULL, state TEXT NOT NULL, job_id TEXT NULL, outcomes_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS host_config (
                key TEXT PRIMARY KEY, value_json TEXT NOT NULL, updated_at TEXT NOT NULL, updated_by TEXT NOT NULL);
            """);
    }
}
