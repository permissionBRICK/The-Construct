using System.Net;
using System.Net.Http.Json;
using Constructd.Store.Records;

namespace Constructd.Store.Client;

/// <summary>Thrown when the store cannot be reached or answers with something other than a store result.</summary>
public sealed class StoreUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A participant's view of one store: the same operations whether the store runs in this process
/// (the engine's own store) or behind HTTP (another engine's, or a stand-alone one).
/// </summary>
public interface IStoreClient
{
    Task<FeedPage> FeedAsync(long since, TimeSpan wait, CancellationToken ct);

    Task<StoredRecord?> GetAsync(string id, CancellationToken ct);

    Task<WriteOutcome> WriteAsync(RecordWrite write, CancellationToken ct);

    Task<BatchOutcome> BatchAsync(IReadOnlyList<RecordWrite> writes, CancellationToken ct);

    Task<long> ActiveEpochAsync(CancellationToken ct);
}

/// <summary>The store in this process, called as <paramref name="principal"/>.</summary>
public sealed class InProcessStoreClient(RecordStore store, StorePrincipal principal) : IStoreClient
{
    public Task<FeedPage> FeedAsync(long since, TimeSpan wait, CancellationToken ct) =>
        store.FeedAsync(principal.Account, since, wait, 500, ct);

    public Task<StoredRecord?> GetAsync(string id, CancellationToken ct) => store.GetAsync(principal.Account, id, ct);

    public Task<WriteOutcome> WriteAsync(RecordWrite write, CancellationToken ct) => store.WriteAsync(principal, write, ct);

    public Task<BatchOutcome> BatchAsync(IReadOnlyList<RecordWrite> writes, CancellationToken ct) => store.BatchAsync(principal, writes, ct);

    public Task<long> ActiveEpochAsync(CancellationToken ct) => store.ActiveEpochAsync(principal.Account, ct);
}

/// <summary>
/// The store behind HTTP. <paramref name="http"/> carries the base address of the store's routes
/// (ending in <c>/api/v1/store/</c>) and the caller's credentials.
/// </summary>
public sealed class HttpStoreClient(HttpClient http) : IStoreClient
{
    public async Task<FeedPage> FeedAsync(long since, TimeSpan wait, CancellationToken ct)
    {
        var url = $"records?since={since}&wait={(int)wait.TotalSeconds}";
        return await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), async response =>
            response.StatusCode == HttpStatusCode.OK ? await Read<FeedPage>(response, ct) : null, ct);
    }

    public async Task<StoredRecord?> GetAsync(string id, CancellationToken ct)
    {
        var found = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "records/" + Uri.EscapeDataString(id)), async response =>
            response.StatusCode switch
            {
                HttpStatusCode.OK => new Box<StoredRecord?>(await Read<StoredRecord>(response, ct)),
                HttpStatusCode.NotFound => new Box<StoredRecord?>(null),
                _ => null,
            }, ct);
        return found.Value;
    }

    public async Task<WriteOutcome> WriteAsync(RecordWrite write, CancellationToken ct)
    {
        return await SendAsync<WriteOutcome>(() =>
        {
            var request = new HttpRequestMessage(write.Deleted ? HttpMethod.Delete : HttpMethod.Put, "records/" + Uri.EscapeDataString(write.Id))
            {
                Content = JsonContent.Create(write, options: StoreJson.Options),
            };
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{write.BaseRevision}\"");
            return request;
        }, async response => response.StatusCode switch
        {
            HttpStatusCode.OK => new WriteOutcome.Accepted(await Read<StoredRecord>(response, ct)),
            HttpStatusCode.PreconditionFailed => new WriteOutcome.Stale(await Read<WriteConflict>(response, ct)),
            _ => await RefusedAsync(response, (status, code, message) => new WriteOutcome.Refused(status, code, message), ct),
        }, ct);
    }

    public async Task<BatchOutcome> BatchAsync(IReadOnlyList<RecordWrite> writes, CancellationToken ct)
    {
        return await SendAsync<BatchOutcome>(() => new HttpRequestMessage(HttpMethod.Post, "records/batch")
        {
            Content = JsonContent.Create(new BatchRequest(writes), options: StoreJson.Options),
        }, async response => response.StatusCode switch
        {
            HttpStatusCode.OK => new BatchOutcome.Accepted(await Read<List<StoredRecord>>(response, ct)),
            HttpStatusCode.PreconditionFailed => new BatchOutcome.Stale(await Read<BatchConflict>(response, ct)),
            _ => await RefusedAsync(response, (status, code, message) => new BatchOutcome.Refused(status, code, message), ct),
        }, ct);
    }

    public async Task<long> ActiveEpochAsync(CancellationToken ct)
    {
        var body = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "epochs"), async response =>
            response.StatusCode == HttpStatusCode.OK ? await Read<EpochBody>(response, ct) : null, ct);
        return body.Active;
    }

    private sealed record EpochBody(long Active);

    private sealed record Box<T>(T Value);

    private sealed record ErrorBody(string? Code, string? Message);

    /// <summary>
    /// A refusal is a store answer only for the 4xx the store itself gives; everything else (a 5xx,
    /// a proxy's page) means the store was not reached and the write stays queued.
    /// </summary>
    private static async Task<T?> RefusedAsync<T>(HttpResponseMessage response, Func<int, string, string, T> make, CancellationToken ct)
        where T : class
    {
        var status = (int)response.StatusCode;
        if (status is < 400 or >= 500) return null;
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>(StoreJson.Options, ct);
            return body?.Code is { } code ? make(status, code, body.Message ?? "") : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync<T>(StoreJson.Options, ct)
        ?? throw new StoreUnavailableException("The store answered with an empty body.");

    private async Task<T> SendAsync<T>(Func<HttpRequestMessage> request, Func<HttpResponseMessage, Task<T?>> handle, CancellationToken ct)
        where T : class
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request(), ct);
        }
        catch (HttpRequestException ex)
        {
            throw new StoreUnavailableException("The store is not reachable.", ex);
        }

        using (response)
        {
            return await handle(response) ?? throw new StoreUnavailableException($"The store answered {(int)response.StatusCode}.");
        }
    }
}
