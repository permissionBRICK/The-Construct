using Constructd.Api.Auth;
using Constructd.Core.Configuration;
using Constructd.Store.Client;
using Constructd.Store.Http;
using Constructd.Store.Identity;
using Constructd.Store.Records;
using Constructd.Sync.Participation;
using Constructd.Sync.Sealing;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        services.TryAddSingleton<StoreHttpHandler>();
        services.AddSingleton<EngineStores>();
        services.AddSingleton(sp =>
        {
            var stores = sp.GetRequiredService<EngineStores>();
            return new EngineDevices(
                sp.GetRequiredService<EngineKeyStore>(),
                sp.GetRequiredService<IRecordKeyProvider>(),
                stores.For,
                stores.AcknowledgeAsync,
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

/// <summary>The handler the HTTP clients of stores elsewhere use; the tests put an in-process server behind it.</summary>
public class StoreHttpHandler
{
    public virtual HttpMessageHandler Create() => new SocketsHttpHandler();
}

/// <summary>
/// The store each account syncs against (9.2): the one named for it in <c>Constructd:Sync:Stores</c>
/// (a URL and the participant token that store issued this engine), else this engine's own, called
/// in process as this engine.
/// </summary>
public sealed class EngineStores(RecordStore store, StoreDirectory directory, EngineStoreIdentity identity, ConstructdOptions options, StoreHttpHandler handler)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IStoreClient> _clients = new(StringComparer.Ordinal);

    public IStoreClient For(string account) => _clients.GetOrAdd(account, a => Remote(a) is { } remote
        ? new HttpStoreClient(Http(remote))
        : identity.Client(store, a));

    /// <summary>Tells the account's store that this engine applied a device's revocation (8.4).</summary>
    public async Task AcknowledgeAsync(string account, string deviceId, CancellationToken ct)
    {
        if (Remote(account) is not { } remote)
        {
            directory.Acknowledge(account, deviceId, identity.Id);
            return;
        }

        using var http = Http(remote);
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync("devices/" + Uri.EscapeDataString(deviceId) + "/acknowledge", null, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new StoreUnavailableException("The store is not reachable.", ex);
        }

        using (response)
        {
            // 409: the store does not list that device as revoked (yet); the next read tries again.
            if (!response.IsSuccessStatusCode && (int)response.StatusCode != 409)
                throw new StoreUnavailableException($"The store answered {(int)response.StatusCode} to the acknowledgement.");
        }
    }

    private RemoteStoreOptions? Remote(string account) =>
        options.Sync.Stores.FirstOrDefault(s => string.Equals(s.Account, account, StringComparison.Ordinal) && s.Url.Length > 0);

    private HttpClient Http(RemoteStoreOptions remote)
    {
        var http = new HttpClient(handler.Create()) { BaseAddress = new Uri(remote.Url.TrimEnd('/') + "/api/v1/store/") };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", remote.Token);
        return http;
    }
}
