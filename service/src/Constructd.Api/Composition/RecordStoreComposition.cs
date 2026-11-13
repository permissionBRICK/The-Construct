using Constructd.Api.Auth;
using Constructd.Core.Configuration;
using Constructd.Store.Client;
using Constructd.Store.Http;
using Constructd.Store.Records;

namespace Constructd.Api.Composition;

/// <summary>
/// The record store every engine embeds (docs/plans/construct-platform-design.md 5.1): its own
/// SQLite file next to the service database (in memory with in-memory persistence), the routes under
/// <c>/api/v1/store</c>, and who is calling (<see cref="UserStorePrincipalResolver"/>).
/// </summary>
public static class RecordStoreComposition
{
    public const string FileName = "store.db";

    public static IServiceCollection AddConstructdRecordStore(this IServiceCollection services, ConstructdOptions options)
    {
        var sqlite = options.EffectivePersistence == PersistenceMode.Sqlite;
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!;
        services.AddRecordStore(sqlite ? Path.Combine(directory, FileName) : ":memory:");
        services.AddSingleton<IStorePrincipalResolver, UserStorePrincipalResolver>();
        services.AddSingleton(_ => EngineStoreIdentity.Load(sqlite ? Path.Combine(directory, EngineStoreIdentity.FileName) : null));
        return services;
    }
}

/// <summary>
/// This engine's participant id in the store (<c>engine:&lt;id&gt;</c>), kept in a file next to the
/// database so it survives restarts. Pairing replaces where it comes from (8.3, 8.7).
/// </summary>
public sealed class EngineStoreIdentity(string id)
{
    public const string FileName = "engine-id";

    public string Id { get; } = id;

    /// <summary>The engine's own store, called as this engine for <paramref name="account"/>.</summary>
    public IStoreClient Client(RecordStore store, string account) => new InProcessStoreClient(store, StorePrincipal.Engine(account, Id));

    public static EngineStoreIdentity Load(string? path)
    {
        if (path is null) return new EngineStoreIdentity(Guid.NewGuid().ToString("n"));
        if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } existing) return new EngineStoreIdentity(existing);
        var id = Guid.NewGuid().ToString("n");
        File.WriteAllText(path, id);
        return new EngineStoreIdentity(id);
    }
}
