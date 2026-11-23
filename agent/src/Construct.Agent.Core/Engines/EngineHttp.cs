using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Construct.Agent.Engines;

/// <summary>
/// HTTPS to an engine. Every connection that may carry a credential checks the engine's certificate
/// against the pin taken when the engine was added (SHA-256 of the certificate, as the Companion pins);
/// redirects are never followed, so a token goes to the pinned engine or nowhere.
/// </summary>
public static class EngineHttp
{
    /// <summary>The engine's base address from what the user pasted: https only, scheme and authority, no path.</summary>
    public static Uri ParseAddress(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "https://" + trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0 ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath.TrimEnd('/').Length > 0)
        {
            throw new AgentFailure(400, "invalid-address", "An engine address is https://host or https://host:port.");
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority));
    }

    public static string PinOf(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    /// <summary>A handler that only talks to an engine presenting the certificate <paramref name="pin"/> names.</summary>
    public static SocketsHttpHandler Pinned(string pin)
    {
        var handler = Base();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null && CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(PinOf(certificate)), System.Text.Encoding.ASCII.GetBytes(pin.ToUpperInvariant()));
        return handler;
    }

    /// <summary>A client for one engine; no credential is added here.</summary>
    public static HttpClient Client(Uri baseAddress, string pin, TimeSpan? timeout = null, HttpMessageHandler? inner = null)
    {
        HttpMessageHandler handler = Pinned(pin);
        if (inner is DelegatingHandler delegating)
        {
            delegating.InnerHandler = handler;
            handler = delegating;
        }

        return new HttpClient(handler) { BaseAddress = baseAddress, Timeout = timeout ?? TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Connects without any credential and reads the certificate the engine presents, for the user to
    /// confirm (4: "the certificate pin taken at that moment"). Also asks the engine's health.
    /// </summary>
    public static async Task<(string Pin, string? Status)> ProbeAsync(Uri baseAddress, CancellationToken ct)
    {
        string? seen = null;
        var handler = Base();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            seen = PinOf(certificate);
            return true;
        };

        using var client = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var response = await client.GetAsync("api/v1/health", ct).ConfigureAwait(false);
            string? status = null;
            if (response.IsSuccessStatusCode)
            {
                var health = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
                status = health.RootElement.TryGetProperty("status", out var value) ? value.GetString() : null;
            }

            return (seen ?? throw new AgentFailure(502, "engine-unreachable", "The engine presented no certificate."), status);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (seen is not null) return (seen, null);
            throw new AgentFailure(502, "engine-unreachable", $"No engine answered at {baseAddress}.");
        }
    }

    private static SocketsHttpHandler Base() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        SslOptions = new SslClientAuthenticationOptions(),
    };
}
