using Microsoft.Data.Sqlite;
namespace Constructd.Api.Admin;

public sealed record DatabaseHealth(string Status, int SchemaVersion);
public static class AdminDbCheck
{
    public static async Task<DatabaseHealth> CheckAsync(string path, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = Path.GetFullPath(path), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(ct);
        await using var check = connection.CreateCommand(); check.CommandText = "PRAGMA quick_check";
        await using (var reader = await check.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct) || reader.GetString(0) != "ok" || await reader.ReadAsync(ct)) return new("failed", 0);
        }
        check.CommandText = "SELECT COALESCE(MAX(id),0) FROM schema_migrations";
        return new("ok", Convert.ToInt32(await check.ExecuteScalarAsync(ct)));
    }
    public static async Task<int> RunAsync(IReadOnlyList<string> args, IServiceProvider services, TextWriter output, CancellationToken ct)
    {
        if (args.Count != 2) return AdminExitCode.Usage;
        var result = await CheckAsync(services.GetRequiredService<Core.Configuration.ConstructdOptions>().DatabasePath, ct);
        await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(result, Windows.Updates.UpdateFiles.Json));
        return result.Status == "ok" ? 0 : 1;
    }
}
