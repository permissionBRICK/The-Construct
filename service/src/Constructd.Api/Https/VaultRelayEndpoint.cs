using System.Net;
using Constructd.Core.Configuration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Constructd.Api.Https;

/// <summary>
/// The internal loopback endpoint the HTTPS shared port relays the vault web address to
/// (docs/https-forwards.md). It is a plain-HTTP Kestrel listener on 127.0.0.1 next to the API's own, so the
/// vault pages are served by the same endpoints as on the API port, and <see cref="Allowed"/> limits what it
/// answers to the phone pages and the device routes, exactly what the vault web address needs.
/// </summary>
public sealed class VaultRelayEndpoint(IServer server, ConstructdOptions options)
{
    private int _port = Math.Max(0, options.Https.VaultRelayPort);

    // The API's own plain-http ports: a plain-http loopback listen URL (fake mode, tests, local
    // development) is bound next to the relay and must not be taken for it.
    private readonly HashSet<int> _apiPorts = (options.ListenUrl ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp ? uri.Port : 0)
        .Where(port => port > 0).ToHashSet();

    /// <summary>The extra listen URL, or null when the service has no listen URL of its own (tests, development).</summary>
    public static string? ListenUrl(ConstructdOptions options) =>
        string.IsNullOrWhiteSpace(options.ListenUrl) ? null : $"http://127.0.0.1:{Math.Max(0, options.Https.VaultRelayPort)}";

    /// <summary>The bound port; zero while the server has not bound it (or there is none).</summary>
    public int Port
    {
        get
        {
            if (Volatile.Read(ref _port) > 0) return _port;
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
            foreach (var bound in addresses ?? [])
            {
                if (!bound.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) || !Uri.TryCreate(bound, UriKind.Absolute, out var uri) ||
                    uri.Port <= 0 || _apiPorts.Contains(uri.Port)) continue;
                Volatile.Write(ref _port, uri.Port);
                break;
            }
            return _port;
        }
    }

    /// <summary>True for a request that arrived through the relay endpoint.</summary>
    public bool IsRelayed(HttpContext http) =>
        Port is > 0 and var port && http.Connection.LocalPort == port && !http.Request.IsHttps &&
        http.Connection.LocalIpAddress is { } local && IPAddress.IsLoopback(local);

    /// <summary>The only paths the relay endpoint answers: the phone pages and the routes they call.</summary>
    public static bool Allowed(PathString path) =>
        path.StartsWithSegments("/vault") ||
        path.StartsWithSegments("/api/v1/vault/approvals") ||
        path.StartsWithSegments("/api/v1/vault/files") ||
        path.Equals("/api/v1/vault/device", StringComparison.Ordinal);
}
