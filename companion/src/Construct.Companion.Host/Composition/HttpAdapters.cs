using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Host.Composition;

public sealed class HttpRemoteApi(Func<HttpClientHandler>? createHandler = null) : IRemoteApi, IDisposable
{
    // Pinned requests share one client per host, authentication mode and pin: the connection and
    // its Negotiate session are reused instead of being rebuilt (and re-authenticated) for every
    // call. A pooled connection was validated against that same pin when it was opened; a new
    // pin is a new client. Unpinned requests get a fresh client so the pin callback always runs.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Entry> clients = new();
    private sealed class Entry(HttpClient client) { public HttpClient Client { get; } = client; public volatile Func<string, bool> VerifyPin = _ => false; }
    public async Task<RemoteResponse> SendAsync(RemoteRequest request, CancellationToken cancellationToken = default)
    {
        Core.Remote.RemoteHost.AssertTransportSafe(request.Url.AbsoluteUri);
        var negotiate = request.Authentication == RemoteAuthentication.Negotiate;
        var entry = request.Pin is null ? Create(negotiate)
            : clients.GetOrAdd(request.Url.Scheme + "://" + request.Url.Authority + "|" + request.Authentication + "|" + request.Pin, _ => Create(negotiate));
        // TLS validates the pin before the HTTP layer can send credentials or a body.
        entry.VerifyPin = request.VerifyPin;
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        if (request.Authentication == RemoteAuthentication.Token && request.Token is {} token)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Reveal());
        if (request.Body is {} body) message.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
        using var lease = request.Pin is null ? entry.Client : null;
        using var response = await entry.Client.SendAsync(message, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonElement? value = null;
        if (!string.IsNullOrWhiteSpace(text)) { using var document = JsonDocument.Parse(text); value = document.RootElement.Clone(); }
        return new((int)response.StatusCode, value);
    }
    private Entry Create(bool negotiate)
    {
        Entry entry;
        if (createHandler is not null)
        {
            var handler = createHandler();
            handler.AllowAutoRedirect = false; handler.UseProxy = false; handler.UseDefaultCredentials = negotiate;
            entry = new(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) });
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null && entry.VerifyPin(certificate.GetCertHashString(HashAlgorithmName.SHA256));
            return entry;
        }
        var sockets = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(15), PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            Credentials = negotiate ? CredentialCache.DefaultCredentials : null, PreAuthenticate = negotiate,
            ConnectCallback = async (context, ct) => await AddressRace.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, TimeSpan.FromSeconds(15), ct),
        };
        entry = new(new HttpClient(sockets) { Timeout = TimeSpan.FromSeconds(30) });
        sockets.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null && entry.VerifyPin(certificate.GetCertHashString(HashAlgorithmName.SHA256));
        return entry;
    }
    public void Dispose() { foreach (var entry in clients.Values) entry.Client.Dispose(); clients.Clear(); }
}

public sealed class HttpUpdateSource(HttpClient? http = null) : IUpdateSource, IDisposable
{
    private const int MaxRedirects = 5;
    private readonly HttpClient client = http ?? new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    // GitHub's releases/latest/download/... answers with redirects; they are followed here so every hop is checked to stay on https.
    public async Task<JsonNode?> GetJsonAsync(Uri url, CancellationToken cancellationToken = default)
    {
        if (url.Scheme != "https") return null;
        try
        {
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("ConstructCompanion/1");
                using var response = await client.SendAsync(request, cancellationToken);
                if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (hop >= MaxRedirects || response.Headers.Location is not { } location) return null;
                    url = location.IsAbsoluteUri ? location : new Uri(url, location);
                    if (url.Scheme != "https") return null;
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.NotFound) return new JsonObject { ["notFound"] = true };
                if (!response.IsSuccessStatusCode) return null;
                return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || e is OperationCanceledException && !cancellationToken.IsCancellationRequested) { return null; }
    }
    public void Dispose() => client.Dispose();
}
