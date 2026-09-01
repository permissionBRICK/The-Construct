using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Microsoft.Data.Sqlite;

namespace Constructd.Sqlite;

/// <summary>
/// Durable VM registry. The registry is the canonical state for remote VMs, so it — not the user's
/// PC — survives reboots on either side (plan §3.3).
/// </summary>
public sealed class SqliteVmRepository(SqliteDatabase database) : IVmRepository
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
            owned.CommandText = "SELECT COUNT(*) FROM vms WHERE owner = @owner;";
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
                                 vm_token_hash, idle_timeout_minutes, idle_action, deleting)
                VALUES (@name, @owner, @cpu, @ramGb, @diskGb, @created, @state, @sshForwardPort,
                        @vmTokenHash, @idleTimeout, @idleAction, @deleting);
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
                   ssh_forward_port = @sshForwardPort, vm_token_hash = @vmTokenHash,
                   idle_timeout_minutes = @idleTimeout, idle_action = @idleAction, deleting = @deleting
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
        .With("@deleting", vm.Deleting ? 1 : 0);

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
        reader.GetBool("deleting"));
}
