using Constructd.Core.Configuration;
using Constructd.Windows.Updates;
using Microsoft.Data.Sqlite;
namespace Constructd.Api.Admin;

public static class AdminMaintenance
{
    public static bool IsMutation(IReadOnlyList<string> args) => (args.ElementAtOrDefault(0)?.ToLowerInvariant(), args.ElementAtOrDefault(1)?.ToLowerInvariant()) is
        ("users", "add" or "remove") or ("tokens", "issue" or "revoke-all") or ("forwards", "reconcile") or ("iso", "build" or "prune");
    public static async Task<IAsyncDisposable?> EnterAsync(IServiceProvider services, IReadOnlyList<string> args, CancellationToken ct)
    {
        var options = services.GetService<ConstructdOptions>();
        if (options is null || options.EffectivePersistence == PersistenceMode.Memory || !IsMutation(args)) return null;
        var hostLock = new FileHostLock(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!);
        var held = await hostLock.TryAcquireAsync("admin.lock", TimeSpan.FromSeconds(30), ct);
        if (held is null) throw new Core.Logic.UpdateException("another administrative operation or an update is running");
        try
        {
            if (File.Exists(options.DatabasePath))
            {
                await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.GetFullPath(options.DatabasePath), Mode=SqliteOpenMode.ReadOnly, Pooling=false }.ToString());
                await c.OpenAsync(ct); await using var cmd = c.CreateCommand();
                cmd.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='host_config'";
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0)
                {
                    cmd.CommandText="SELECT value_json FROM host_config WHERE key='maintenance'";
                    var json = (string?)await cmd.ExecuteScalarAsync(ct);
                    if (json is not null && System.Text.Json.JsonSerializer.Deserialize<Core.Domain.MaintenanceMarker>(json, UpdateFiles.Json)?.State != Core.Domain.MaintenanceState.Open)
                        throw new Core.Logic.UpdateException("another administrative operation or an update is running");
                }
            }
            return held;
        }
        catch { await held.DisposeAsync(); throw; }
    }
}
