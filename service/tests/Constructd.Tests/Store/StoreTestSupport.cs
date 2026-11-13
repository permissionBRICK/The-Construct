using System.Text.Json.Nodes;
using Constructd.Store.Client;
using Constructd.Store.Http;
using Constructd.Store.Records;
using Constructd.Sync.Sealing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace Constructd.Tests.Store;

/// <summary>A clock the store tests move by hand.</summary>
public sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A store client whose link can be cut, for the partition tests (C.4).</summary>
public sealed class PartitionableClient(IStoreClient inner) : IStoreClient
{
    public bool Partitioned { get; set; }

    /// <summary>When set, the next write reaches the store but its answer is lost.</summary>
    public bool LoseNextAnswer { get; set; }

    private void Check()
    {
        if (Partitioned) throw new StoreUnavailableException("partitioned");
    }

    public Task<FeedPage> FeedAsync(long since, TimeSpan wait, CancellationToken ct) { Check(); return inner.FeedAsync(since, wait, ct); }

    public Task<StoredRecord?> GetAsync(string id, CancellationToken ct) { Check(); return inner.GetAsync(id, ct); }

    public async Task<WriteOutcome> WriteAsync(RecordWrite write, CancellationToken ct)
    {
        Check();
        var outcome = await inner.WriteAsync(write, ct);
        if (LoseNextAnswer)
        {
            LoseNextAnswer = false;
            throw new StoreUnavailableException("answer lost");
        }

        return outcome;
    }

    public Task<BatchOutcome> BatchAsync(IReadOnlyList<RecordWrite> writes, CancellationToken ct) { Check(); return inner.BatchAsync(writes, ct); }

    public Task<long> ActiveEpochAsync(CancellationToken ct) { Check(); return inner.ActiveEpochAsync(ct); }
}

/// <summary>The stand-alone store host, started in process with configured participants.</summary>
public sealed class StandaloneStoreFixture : IAsyncDisposable
{
    public const string Account = "alice";

    private readonly WebApplication _app;

    public StandaloneStoreFixture(string directory, params (string Token, ParticipantKind Kind, string Id)[] participants)
    {
        var settings = new List<string> { $"--Store:DatabasePath={Path.Combine(directory, "store.db")}" };
        for (var i = 0; i < participants.Length; i++)
        {
            settings.Add($"--Store:Participants:{i}:Token={participants[i].Token}");
            settings.Add($"--Store:Participants:{i}:Account={Account}");
            settings.Add($"--Store:Participants:{i}:Kind={participants[i].Kind}");
            settings.Add($"--Store:Participants:{i}:Id={participants[i].Id}");
        }

        _app = StandaloneStore.Build([.. settings], builder => builder.WebHost.UseTestServer());
        _app.StartAsync().GetAwaiter().GetResult();
    }

    public RecordStore Store => (RecordStore)_app.Services.GetService(typeof(RecordStore))!;

    public HttpClient Http(string? token)
    {
        var client = _app.GetTestClient();
        client.BaseAddress = new Uri(client.BaseAddress!, "/api/v1/store/");
        if (token is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    public IStoreClient Client(string token) => new HttpStoreClient(Http(token));

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>A temporary directory removed at the end of a test.</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "constructd-store-" + Guid.NewGuid().ToString("n"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}

public static class StoreWrites
{
    public static readonly byte[] Opaque = [1, 2, 3];

    public static RecordWrite Write(StorePrincipal by, string id, string kind, long baseRevision, bool deleted = false, long epoch = 1, string? writeId = null) =>
        new(id, kind, epoch, by.Actor, baseRevision, writeId ?? Guid.NewGuid().ToString("n"), deleted, deleted ? null : Opaque);

    public static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    public static async Task<JsonObject> OpenAsync(IRecordKeyProvider keys, StoredRecord record) =>
        RecordSealer.Open(await keys.GetKeyAsync(StandaloneStoreFixture.Account, record.Epoch, default), record.Envelope, record.Ciphertext!);
}
