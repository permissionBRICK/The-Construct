using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Microsoft.Data.Sqlite;

namespace Constructd.Sqlite;

/// <summary>
/// Durable forward state (plan §4.4/§4.6). This is what
/// <see cref="IPortForwardManager.ReconcileAsync"/> rebuilds the host's rules and its port allocators
/// from after a restart, and why a forward outlives both the user's PC and the service process.
/// </summary>
public sealed class SqliteForwardStore(SqliteDatabase database) : IForwardStore
{
    public async Task<PortForward?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM forwards WHERE id = @id;";
        command.With("@id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<PortForward>> ListAsync(string? vmName, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = vmName is null
            ? "SELECT * FROM forwards ORDER BY created, id;"
            : "SELECT * FROM forwards WHERE vm_name = @vmName ORDER BY created, id;";

        if (vmName is not null)
        {
            command.With("@vmName", vmName);
        }

        var forwards = new List<PortForward>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            forwards.Add(Read(reader));
        }

        return forwards;
    }

    public async Task<int> CountByVmAsync(string vmName, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM forwards WHERE vm_name = @vmName;";
        command.With("@vmName", vmName);

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task AddAsync(PortForward forward, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(forward);

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO forwards (id, vm_name, vm_port, public_port, target, label, created)
            VALUES (@id, @vmName, @vmPort, @publicPort, @target, @label, @created);
            """;
        command
            .With("@id", forward.Id)
            .With("@vmName", forward.VmName)
            .With("@vmPort", forward.VmPort)
            .With("@publicPort", forward.PublicPort)
            .With("@target", forward.Target.ToString())
            .With("@label", forward.Label)
            .With("@created", SqliteDatabase.Text(forward.Created));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SetAckAsync(string id, ForwardAck ack, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ack);

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE forwards
               SET ack_status = @status,
                   ack_local_port = @localPort,
                   ack_host_label = @hostLabel,
                   ack_message = @message,
                   ack_at = @at
             WHERE id = @id;
            """;
        command
            .With("@id", id)
            .With("@status", ack.Status.ToString())
            .With("@localPort", ack.LocalPort)
            .With("@hostLabel", ack.HostLabel)
            .With("@message", ack.Message)
            .With("@at", SqliteDatabase.Text(ack.At));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM forwards WHERE id = @id;";
        command.With("@id", id);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static PortForward Read(SqliteDataReader reader) => new(
        reader.GetString("id"),
        reader.GetString("vm_name"),
        reader.GetInt("vm_port"),
        reader.GetIntOrNull("public_port"),
        SqliteDatabase.ReadEnum<ForwardTarget>(reader.GetString("target")),
        reader.GetString("label"),
        SqliteDatabase.ReadTime(reader.GetString("created")),
        ReadAck(reader));

    /// <summary>The ack columns, or null when nobody has acked this forward.</summary>
    private static ForwardAck? ReadAck(SqliteDataReader reader)
    {
        var status = reader.GetStringOrNull("ack_status");
        if (status is null)
        {
            return null;
        }

        // ack_at is written in the same statement as ack_status, so a row with a status always has
        // one; MinValue is the "cannot happen, do not throw over it" fallback for a hand-edited file.
        var at = reader.GetStringOrNull("ack_at");
        return new ForwardAck(
            SqliteDatabase.ReadEnum<AckStatus>(status),
            reader.GetIntOrNull("ack_local_port"),
            reader.GetStringOrNull("ack_host_label"),
            reader.GetStringOrNull("ack_message") ?? string.Empty,
            at is null ? DateTimeOffset.MinValue : SqliteDatabase.ReadTime(at));
    }
}
