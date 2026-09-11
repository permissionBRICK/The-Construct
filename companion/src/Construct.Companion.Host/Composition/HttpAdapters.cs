using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Host.Composition;

public sealed class HttpRemoteApi(Func<HttpClientHandler>? createHandler = null) : IRemoteApi
{
    public async Task<RemoteResponse> SendAsync(RemoteRequest request, CancellationToken cancellationToken = default)
    {
        Core.Remote.RemoteHost.AssertTransportSafe(request.Url.AbsoluteUri);
        // TLS validates the pin before the HTTP layer can send credentials or a body.
        using var handler = createHandler?.Invoke() ?? new HttpClientHandler();
        handler.AllowAutoRedirect = false; handler.UseProxy = false;
        handler.UseDefaultCredentials = request.Authentication == RemoteAuthentication.Negotiate;
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
            request.VerifyPin(certificate.GetCertHashString(HashAlgorithmName.SHA256));
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        if (request.Authentication == RemoteAuthentication.Token && request.Token is {} token)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Reveal());
        if (request.Body is {} body) message.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(message, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonElement? value = null;
        if (!string.IsNullOrWhiteSpace(text)) { using var document = JsonDocument.Parse(text); value = document.RootElement.Clone(); }
        return new((int)response.StatusCode, value);
    }
}
public sealed class HttpUpdateSource(HttpClient? http = null) : IUpdateSource, IDisposable
{
    private readonly HttpClient client = http ?? new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    public async Task<JsonNode?> GetJsonAsync(Uri url, CancellationToken cancellationToken = default)
    {
        if (url.Scheme != "https") return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("ConstructCompanion/1");
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return new JsonObject { ["notFound"] = true };
            if (!response.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || e is OperationCanceledException && !cancellationToken.IsCancellationRequested) { return null; }
    }
    public void Dispose() => client.Dispose();
}
