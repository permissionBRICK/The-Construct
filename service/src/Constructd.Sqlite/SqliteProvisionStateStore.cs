using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Sqlite;

/// <summary>The desired and applied provisioning state per VM (<c>vm_provision_state</c>).</summary>
public sealed class SqliteProvisionStateStore(SqliteDatabase database) : IVmDesiredStateSource, IVmAppliedStateStore
{
    public async Task<StoredDesiredState?> GetDesiredAsync(string vmName, CancellationToken ct) =>
        WireJson.Read<StoredDesiredState>(await ReadAsync(vmName, "desired_json", ct));

    public Task SetDesiredAsync(string vmName, StoredDesiredState desired, CancellationToken ct) =>
        WriteAsync(vmName, "desired_json", WireJson.Serialize(desired)!, ct);

    public async Task RemoveAsync(string vmName, CancellationToken ct)
    {
        await using var c = await database.OpenAsync(ct); await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM vm_provision_state WHERE vm=@vm"; cmd.With("@vm", vmName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<VmAppliedState?> GetAppliedAsync(string vmName, CancellationToken ct) =>
        WireJson.Read<VmAppliedState>(await ReadAsync(vmName, "applied_json", ct));

    public Task SetAppliedAsync(string vmName, VmAppliedState applied, CancellationToken ct) =>
        WriteAsync(vmName, "applied_json", WireJson.Serialize(applied)!, ct);

    private async Task<string?> ReadAsync(string vmName, string column, CancellationToken ct)
    {
        await using var c = await database.OpenAsync(ct); await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM vm_provision_state WHERE vm=@vm"; cmd.With("@vm", vmName);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private async Task WriteAsync(string vmName, string column, string json, CancellationToken ct)
    {
        await using var c = await database.OpenAsync(ct); await using var cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT INTO vm_provision_state (vm, {column}) VALUES (@vm, @json) ON CONFLICT(vm) DO UPDATE SET {column}=@json";
        cmd.With("@vm", vmName).With("@json", json);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
