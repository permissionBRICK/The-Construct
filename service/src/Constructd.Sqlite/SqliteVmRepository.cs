using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Microsoft.Data.Sqlite;

namespace Constructd.Sqlite;

/// <summary>
/// Durable VM registry. The registry is the canonical state for remote VMs, so it — not the user's
/// PC — survives reboots on either side (plan §3.3).
/// </summary>
public sealed partial class SqliteVmRepository(SqliteDatabase database, IClock? clock = null) : IVmRepository, IVmDelegationRepository, IVmMetadataStore
{
    public async Task<Vm?> GetAsync(string name, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM vms WHERE name = @name;";
        command.With("@name", name);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<Vm>> ListAsync(string? owner, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = owner is null
            ? "SELECT * FROM vms ORDER BY name;"
            : "SELECT * FROM vms WHERE owner = @owner ORDER BY name;";

        if (owner is not null)
        {
            command.With("@owner", owner);
        }

        var vms = new List<Vm>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            vms.Add(Read(reader));
        }

        return vms;
    }

    /// <summary>
    /// Name check, quota check and insert in one <c>IMMEDIATE</c> transaction: two concurrent
    /// <c>POST /vms</c> calls cannot both see "one below the quota" and both insert.
    /// </summary>
    public async Task<VmAddOutcome> AddAsync(Vm vm, int maxVms, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vm);

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT COUNT(*) FROM vms WHERE name = @name;";
            exists.With("@name", vm.Name);
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0)
            {
                return VmAddOutcome.NameTaken;
            }
        }

        await using (var owned = connection.CreateCommand())
        {
            owned.Transaction = transaction;
            owned.CommandText = "SELECT COUNT(*) FROM vms WHERE owner = @owner AND kind='primary';";
            owned.With("@owner", vm.Owner);
            if (Convert.ToInt64(await owned.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) >= maxVms)
            {
                return VmAddOutcome.QuotaExceeded;
            }
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO vms (name, owner, cpu, ram_gb, disk_gb, created, state, ssh_forward_port,
                                 vm_token_hash, idle_timeout_minutes, idle_action, deleting, power_generation, kind, parent, sharing, vm_token_kind, ram_mb, incarnation, lease_requested_text, lease_requested_seconds, lease_activated_at, lease_expires_at, lease_state, lease_version, lease_last_attempt_at, lease_last_outcome, hardware_json, guest_construct_commit, guest_provisioned_at, guest_reinstalled_at, guest_reported_at, guest_provenance, guest_last_attempt_at, guest_last_attempt_outcome, observed_created_at, observed_last_boot_at, observed_addresses_json, observed_storage_problem, child_creation_closed, current_job_id, source_commit)
                VALUES (@name, @owner, @cpu, @ramGb, @diskGb, @created, @state, @sshForwardPort,
                        @vmTokenHash, @idleTimeout, @idleAction, @deleting, @power_generation, @kind, @parent, @sharing, @vm_token_kind, @ram_mb, @incarnation, @lease_requested_text, @lease_requested_seconds, @lease_activated_at, @lease_expires_at, @lease_state, @lease_version, @lease_last_attempt_at, @lease_last_outcome, @hardware_json, @guest_construct_commit, @guest_provisioned_at, @guest_reinstalled_at, @guest_reported_at, @guest_provenance, @guest_last_attempt_at, @guest_last_attempt_outcome, @observed_created_at, @observed_last_boot_at, @observed_addresses_json, @observed_storage_problem, @child_creation_closed, @current_job_id, @source_commit);
                """;
            Bind(insert, vm);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return VmAddOutcome.Added;
    }

    public async Task<bool> UpdateAsync(Vm vm, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vm);

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE vms
               SET owner = @owner, cpu = @cpu, ram_gb = @ramGb, disk_gb = @diskGb, state = @state,
                   ssh_forward_port = @sshForwardPort,
                   vm_token_hash = CASE WHEN @deleting = 1 THEN NULL ELSE vm_token_hash END,
                   idle_timeout_minutes = @idleTimeout, idle_action = @idleAction, deleting = MAX(deleting, @deleting)
             WHERE name = @name;
            """;
        Bind(command, vm);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var activity = connection.CreateCommand();
        activity.CommandText = "DELETE FROM activity WHERE vm_name = @name;";
        activity.With("@name", name);
        await activity.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM vms WHERE name = @name;";
        command.With("@name", name);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<int> CountByOwnerAsync(string owner, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM vms WHERE owner = @owner;";
        command.With("@owner", owner);

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task SaveActivityAsync(ActivityReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO activity (vm_name, busy, reasons, reported_at)
            VALUES (@vmName, @busy, @reasons, @reportedAt)
            ON CONFLICT(vm_name) DO UPDATE
               SET busy = excluded.busy, reasons = excluded.reasons, reported_at = excluded.reported_at;
            """;
        command
            .With("@vmName", report.VmName)
            .With("@busy", report.Busy ? 1 : 0)
            .With("@reasons", JsonSerializer.Serialize(report.Reasons))
            .With("@reportedAt", SqliteDatabase.Text(report.ReportedAt));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ActivityReport?> GetLatestActivityAsync(string vmName, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM activity WHERE vm_name = @vmName;";
        command.With("@vmName", vmName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var reasons = JsonSerializer.Deserialize<List<string>>(reader.GetString("reasons")) ?? [];
        return new ActivityReport(
            reader.GetString("vm_name"),
            reader.GetBool("busy"),
            reasons,
            SqliteDatabase.ReadTime(reader.GetString("reported_at")));
    }

    private static void Bind(SqliteCommand command, Vm vm) => command
        .With("@name", vm.Name)
        .With("@owner", vm.Owner)
        .With("@cpu", vm.Cpu)
        .With("@ramGb", vm.RamGb)
        .With("@diskGb", vm.DiskGb)
        .With("@created", SqliteDatabase.Text(vm.Created))
        .With("@state", vm.State.ToString())
        .With("@sshForwardPort", vm.SshForwardPort)
        .With("@vmTokenHash", vm.VmTokenHash)
        .With("@idleTimeout", vm.IdlePolicy.TimeoutMinutes)
        .With("@idleAction", vm.IdlePolicy.Action.ToString())
        .With("@deleting", vm.Deleting ? 1 : 0)
        .With("@power_generation", vm.PowerGeneration)
        .With("@kind", WireJson.Enum(vm.Kind))
        .With("@parent", vm.Parent)
        .With("@sharing", WireJson.Enum(vm.Sharing))
        .With("@vm_token_kind", WireJson.Enum(vm.TokenKind))
        .With("@ram_mb", vm.RamMb)
        .With("@incarnation", vm.Incarnation)
        .With("@lease_requested_text", vm.Lease?.RequestedText)
        .With("@lease_requested_seconds", vm.Lease?.RequestedSeconds)
        .With("@lease_activated_at", SqliteDatabase.TextOrNull(vm.Lease?.ActivatedAt))
        .With("@lease_expires_at", SqliteDatabase.TextOrNull(vm.Lease?.ExpiresAt))
        .With("@lease_state", vm.Lease is null ? null : WireJson.Enum(vm.Lease.State))
        .With("@lease_version", vm.Lease?.Version ?? 0)
        .With("@lease_last_attempt_at", SqliteDatabase.TextOrNull(vm.Lease?.LastExpiryAttemptAt))
        .With("@lease_last_outcome", vm.Lease?.LastExpiryOutcome)
        .With("@hardware_json", WireJson.Serialize(vm.Hardware))
        .With("@guest_construct_commit", vm.Guest?.ConstructCommit)
        .With("@guest_provisioned_at", SqliteDatabase.TextOrNull(vm.Guest?.ProvisionedAt))
        .With("@guest_reinstalled_at", SqliteDatabase.TextOrNull(vm.Guest?.ReinstalledAt))
        .With("@guest_reported_at", SqliteDatabase.TextOrNull(vm.Guest?.ReportedAt))
        .With("@guest_provenance", WireJson.Enum(vm.Guest?.Provenance ?? GuestReportProvenance.Unknown))
        .With("@guest_last_attempt_at", SqliteDatabase.TextOrNull(vm.Guest?.LastAttemptAt))
        .With("@guest_last_attempt_outcome", vm.Guest?.LastAttemptOutcome)
        .With("@observed_created_at", SqliteDatabase.TextOrNull(vm.Observed?.CreatedAt))
        .With("@observed_last_boot_at", SqliteDatabase.TextOrNull(vm.Observed?.LastBootAt))
        .With("@observed_addresses_json", WireJson.Serialize(vm.Observed?.Addresses))
        .With("@observed_storage_problem", vm.Observed?.StorageProblem)
        .With("@child_creation_closed", vm.ChildCreationClosed ? 1 : 0)
        .With("@current_job_id", vm.CurrentJobId)
        .With("@source_commit", vm.SourceCommit);

    private static Vm Read(SqliteDataReader reader) => new(
        reader.GetString("name"),
        reader.GetString("owner"),
        reader.GetInt("cpu"),
        reader.GetInt("ram_gb"),
        reader.GetInt("disk_gb"),
        SqliteDatabase.ReadTime(reader.GetString("created")),
        SqliteDatabase.ReadEnum<VmState>(reader.GetString("state")),
        reader.GetIntOrNull("ssh_forward_port"),
        reader.GetStringOrNull("vm_token_hash"),
        new IdlePolicy(
            reader.GetInt("idle_timeout_minutes"),
            SqliteDatabase.ReadEnum<IdleAction>(reader.GetString("idle_action"))),
        Vm.NoForwards,
        reader.GetBool("deleting"),
        reader.GetLong("power_generation"),
        reader.GetEnum<VmKind>("kind"),
        reader.GetStringOrNull("parent"),
        reader.GetEnum<SharingScope>("sharing"),
        reader.GetEnum<VmTokenKind>("vm_token_kind"),
        reader.GetIntOrNull("ram_mb"),
        reader.GetStringOrNull("incarnation"),
        ReadLease(reader),
        WireJson.Read<ChildHardware>(reader.GetStringOrNull("hardware_json")),
        ReadGuest(reader),
        ReadObservation(reader),
        reader.GetBool("child_creation_closed"),
        reader.GetStringOrNull("current_job_id"),
        reader.GetStringOrNull("source_commit"));

    private static Lease? ReadLease(SqliteDataReader r) => r.GetStringOrNull("lease_requested_text") is string text
        ? new(text, r.GetLongOrNull("lease_requested_seconds"), r.GetTime("lease_activated_at"), r.GetTime("lease_expires_at"),
            r.GetEnum<LeaseState>("lease_state"), r.GetLong("lease_version"), r.GetTime("lease_last_attempt_at"), r.GetStringOrNull("lease_last_outcome")) : null;
    private static GuestReport? ReadGuest(SqliteDataReader r) => r.GetStringOrNull("guest_reported_at") is null && r.GetStringOrNull("guest_last_attempt_at") is null
        ? null : new(r.GetStringOrNull("guest_construct_commit"), r.GetTime("guest_provisioned_at"), r.GetTime("guest_reinstalled_at"),
            r.GetTime("guest_reported_at"), r.GetEnum<GuestReportProvenance>("guest_provenance"), r.GetTime("guest_last_attempt_at"), r.GetStringOrNull("guest_last_attempt_outcome"));
    private static HostObservation? ReadObservation(SqliteDataReader r) => r.GetStringOrNull("observed_addresses_json") is null
        && r.GetStringOrNull("observed_created_at") is null && r.GetStringOrNull("observed_last_boot_at") is null && r.GetStringOrNull("observed_storage_problem") is null ? null
        : new(r.GetTime("observed_created_at"), r.GetTime("observed_last_boot_at"),
            WireJson.Read<IReadOnlyList<GuestAddress>>(r.GetStringOrNull("observed_addresses_json")) ?? [], r.GetStringOrNull("observed_storage_problem"));
}
