using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Sqlite;

public sealed class SqliteTokenUsageStore(SqliteDatabase database) : ITokenUsageStore
{
    public async Task<bool> UpsertAsync(Vm vm, IReadOnlyList<TokenUsageDay> days, DateTimeOffset reportedAt, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var fence = connection.CreateCommand();
        fence.Transaction = transaction;
        fence.CommandText = "SELECT COUNT(*) FROM vms WHERE name=@vm AND owner=@owner AND incarnation IS @incarnation AND deleting=0";
        fence.With("@vm", vm.Name).With("@owner", vm.Owner).With("@incarnation", vm.Incarnation);
        if (Convert.ToInt32(await fence.ExecuteScalarAsync(ct)) != 1) return false;
        foreach (var day in days)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO token_usage(vm_name,owner,tool,day,input_tokens,output_tokens,cache_create_tokens,
                    cache_read_tokens,total_tokens,cost_usd_micros,models_json,reported_at)
                VALUES(@vm,@owner,@tool,@day,@input,@output,@create,@read,@total,@cost,@models,@at)
                ON CONFLICT(vm_name,tool,day) DO UPDATE SET owner=excluded.owner,
                    input_tokens=excluded.input_tokens, output_tokens=excluded.output_tokens,
                    cache_create_tokens=excluded.cache_create_tokens, cache_read_tokens=excluded.cache_read_tokens,
                    total_tokens=excluded.total_tokens, cost_usd_micros=excluded.cost_usd_micros,
                    models_json=excluded.models_json, reported_at=excluded.reported_at, vm_deleted_at=NULL
                """;
            command.With("@vm", vm.Name).With("@owner", vm.Owner).With("@tool", day.Tool).With("@day", day.Day)
                .With("@input", day.InputTokens).With("@output", day.OutputTokens).With("@create", day.CacheCreateTokens)
                .With("@read", day.CacheReadTokens).With("@total", day.TotalTokens).With("@cost", day.CostUsdMicros)
                .With("@models", day.ModelsJson).With("@at", SqliteDatabase.Text(reportedAt));
            await command.ExecuteNonQueryAsync(ct);
        }
        transaction.Commit();
        return true;
    }

    public async Task<IReadOnlyList<TokenUsageRow>> ListAsync(string? owner, string? vm, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM token_usage WHERE (@owner IS NULL OR owner=@owner) AND (@vm IS NULL OR vm_name=@vm)";
        command.With("@owner", owner).With("@vm", vm);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<TokenUsageRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new(reader.GetString("vm_name"), reader.GetString("owner"),
                new(reader.GetString("day"), reader.GetString("tool"), reader.GetInt64(reader.GetOrdinal("input_tokens")),
                    reader.GetInt64(reader.GetOrdinal("output_tokens")), reader.GetInt64(reader.GetOrdinal("cache_create_tokens")),
                    reader.GetInt64(reader.GetOrdinal("cache_read_tokens")), reader.GetInt64(reader.GetOrdinal("total_tokens")),
                    reader.GetInt64(reader.GetOrdinal("cost_usd_micros")), reader.GetString("models_json")),
                SqliteDatabase.ReadTime(reader.GetString("reported_at")), SqliteDatabase.ReadTimeOrNull(reader.GetStringOrNull("vm_deleted_at"))));
        return rows;
    }

    public async Task<int> PruneAsync(DateOnly cutoff, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        using var command = connection.CreateCommand();
        // A month expires only when its last day is outside retention.
        command.CommandText = """
            DELETE FROM token_usage WHERE
                CASE WHEN length(day)=7 THEN date(day || '-01','+1 month','-1 day') ELSE day END < @cutoff
            """;
        command.With("@cutoff", cutoff.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync(ct);
    }
}
