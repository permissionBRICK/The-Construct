using Constructd.Core.Abstractions;
namespace Constructd.Sqlite;

public sealed class SqliteHostConfigStore(SqliteDatabase database, IClock clock) : IHostConfigStore, IHostConfigMetadata
{
    public async Task<T?> GetAsync<T>(string section, CancellationToken ct) where T : class
    {
        await using var c = await database.OpenAsync(ct); await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM host_config WHERE key=@key"; cmd.With("@key", section);
        return WireJson.Read<T>((string?)await cmd.ExecuteScalarAsync(ct));
    }
    public async Task SetAsync<T>(string section, T value, string updatedBy, CancellationToken ct) where T : class =>
        _ = await TrySetSectionsAsync([new(section, WireJson.Serialize(value)!, clock.UtcNow, updatedBy)], new Dictionary<string, DateTimeOffset?>(), ct);
    public Task<bool> TrySetAsync<T>(string section, T value, string updatedBy, DateTimeOffset? expectedUpdatedAt, CancellationToken ct) where T : class =>
        TrySetSectionsAsync([new(section, WireJson.Serialize(value)!, clock.UtcNow, updatedBy)], new Dictionary<string, DateTimeOffset?> { [section] = expectedUpdatedAt }, ct);
    public async Task<IReadOnlyList<HostConfigSection>> ListSectionsAsync(CancellationToken ct)
    {
        await using var c = await database.OpenAsync(ct); await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM host_config";
        await using var r = await cmd.ExecuteReaderAsync(ct); var sections = new List<HostConfigSection>();
        while (await r.ReadAsync(ct)) sections.Add(new(r.GetString("key"), r.GetString("value_json"), r.GetTime("updated_at")!.Value, r.GetString("updated_by")));
        return sections;
    }
    public async Task<bool> TrySetSectionsAsync(IReadOnlyList<HostConfigSection> sections, IReadOnlyDictionary<string, DateTimeOffset?> expected, CancellationToken ct)
    {
        await using var c = await database.OpenAsync(ct); await using var tx = c.BeginTransaction(deferred: false);
        foreach (var (key, at) in expected)
        {
            await using var check = c.CreateCommand(); check.Transaction = tx;
            check.CommandText = "SELECT updated_at FROM host_config WHERE key=@key"; check.With("@key", key);
            if (SqliteDatabase.ReadTimeOrNull(await check.ExecuteScalarAsync(ct)) != at) return false;
        }
        foreach (var section in sections)
        {
            await using var probe = c.CreateCommand(); probe.Transaction = tx;
            probe.CommandText = "SELECT updated_at FROM host_config WHERE key=@key"; probe.With("@key", section.Section);
            var old = SqliteDatabase.ReadTimeOrNull(await probe.ExecuteScalarAsync(ct));
            var at = old is not null && old >= section.UpdatedAt ? old.Value.AddTicks(1) : section.UpdatedAt;
            await using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO host_config VALUES (@key,@value,@at,@by)
                ON CONFLICT(key) DO UPDATE SET value_json=@value,updated_at=@at,updated_by=@by
                """;
            cmd.With("@key", section.Section).With("@value", section.ValueJson).With("@at", SqliteDatabase.Text(at)).With("@by", section.UpdatedBy);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct); return true;
    }
}
