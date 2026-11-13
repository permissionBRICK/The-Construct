using Constructd.Api.Auth;
using Constructd.Core.Configuration;
using Constructd.Store.Client;
using Constructd.Store.Http;
using Constructd.Store.Identity;
using Constructd.Store.Records;
using Constructd.Sync.Participation;
using Constructd.Sync.Sealing;

namespace Constructd.Api.Composition;

/// <summary>
/// The record store every engine embeds (docs/plans/construct-platform-design.md 5.1): its own
/// SQLite file next to the service database (in memory with in-memory persistence), the routes under
/// <c>/api/v1/store</c>, whose callers the store's directory resolves from the tokens it issued
/// (8.1). Next to it this engine as a participant (8.7): its host key, the keys devices granted it,
/// the device tokens it issued, and <c>D</c> read from the grant.
/// </summary>
public static class RecordStoreComposition
{
    public const string FileName = "store.db";
    public const string EngineKeysFileName = "engine-keys.db";

    public static IServiceCollection AddConstructdRecordStore(this IServiceCollection services, ConstructdOptions options)
    {
        var sqlite = options.EffectivePersistence == PersistenceMode.Sqlite;
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!;
        services.AddRecordStore(sqlite ? Path.Combine(directory, FileName) : ":memory:");
        services.AddSingleton<IFirstDeviceAuthority, SignedInUserFirstDevice>();
        services.AddSingleton(_ => EngineStoreIdentity.Load(sqlite ? Path.Combine(directory, EngineStoreIdentity.FileName) : null));
        services.AddSingleton(sp => new EngineKeyStore(sqlite ? Path.Combine(directory, EngineKeysFileName) : ":memory:", sp.GetRequiredService<EngineStoreIdentity>().Id));
        services.AddSingleton<IRecordKeyProvider>(sp => new GrantedRecordKeyProvider(sp.GetRequiredService<EngineKeyStore>()));
        services.AddSingleton(sp =>
        {
            var identity = sp.GetRequiredService<EngineStoreIdentity>();
            var store = sp.GetRequiredService<RecordStore>();
            var storeDirectory = sp.GetRequiredService<StoreDirectory>();
            return new EngineDevices(
                sp.GetRequiredService<EngineKeyStore>(),
                sp.GetRequiredService<IRecordKeyProvider>(),
                account => identity.Client(store, account),
                (account, deviceId, _) =>
                {
                    storeDirectory.Acknowledge(account, deviceId, identity.Id);
                    return Task.CompletedTask;
                },
                sp.GetService<TimeProvider>());
        });
        return services;
    }
}

/// <summary>
/// In the engine's built-in store the first device of an account is registered by the signed-in
/// user who owns it (5.4): an enrolled user, not a VM or a vault approval device.
/// </summary>
public sealed class SignedInUserFirstDevice : IFirstDeviceAuthority
{
    public string? AccountOf(HttpContext context)
    {
        var user = context.User;
        if (!user.IsKnownUser() || user.IsVmToken() || user.IsVaultDevice()) return null;
        var name = user.NameOrEmpty();
        return name.Length == 0 ? null : name;
    }
}

/// <summary>
/// This engine's participant id in the store (<c>engine:&lt;id&gt;</c>), kept in a file next to the
/// database so it survives restarts.
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
