using Constructd.Core.Abstractions;
namespace Constructd.Sqlite;

public sealed class SqliteNetworkRuleStore(SqliteDatabase database) : INetworkRuleStore
{
    public async Task ReplaceAsync(string vmName, IReadOnlyList<NetworkRule> rules, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        using var delete = connection.CreateCommand(); delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM network_rules WHERE vm_name=@vm;"; delete.With("@vm", vmName);
        await delete.ExecuteNonQueryAsync(ct);
        foreach (var rule in rules)
        {
            using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
            cmd.CommandText = "INSERT INTO network_rules VALUES (@id,@vm,@peer,@kind,@state,@created,@updated);";
            cmd.With("@id", rule.Id).With("@vm", vmName).With("@peer", rule.Peer).With("@kind", rule.Kind)
                .With("@state", rule.State).With("@created", SqliteDatabase.Text(rule.Created)).With("@updated", SqliteDatabase.Text(rule.Updated));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        transaction.Commit();
    }
    public async Task<IReadOnlyList<NetworkRule>> ListAsync(string? vmName, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct); using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, vm_name, peer, kind, state, created, updated FROM network_rules" + (vmName is null ? ";" : " WHERE vm_name=@vm;");
        if (vmName is not null) cmd.With("@vm", vmName);
        var result = new List<NetworkRule>(); using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), SqliteDatabase.ReadTime(reader.GetString(5)), SqliteDatabase.ReadTime(reader.GetString(6))));
        return result;
    }
    public async Task RememberAddressAsync(string vmName, string address, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct); using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO network_address_history VALUES (@vm,@address);";
        cmd.With("@vm", vmName).With("@address", address); await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<IReadOnlyList<string>> PreviousAddressesAsync(string vmName, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct); using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT address FROM network_address_history WHERE vm_name=@vm;"; cmd.With("@vm", vmName);
        var result = new List<string>(); using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0)); return result;
    }
}
