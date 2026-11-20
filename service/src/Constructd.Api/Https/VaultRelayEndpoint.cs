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

    // The ports of the API's own fixed plain-http listeners on 127.0.0.1: a plain-http loopback listen URL
    // (fake mode, tests, local development) is bound next to the relay and must not be taken for it. Only
    // 127.0.0.1 counts: a listener on another address can share a port number with a 127.0.0.1 one.
    private readonly HashSet<int> _apiPorts = (options.ListenUrl ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.Host == "127.0.0.1" ? uri.Port : 0)
        .Where(port => port > 0).ToHashSet();

    // The API's own plain-http loopback listeners on a dynamic port: they are told apart from the relay only
    // by Kestrel's bind order, so the relay is known once one more such address is bound.
    private readonly int _dynamicApiListeners = (options.ListenUrl ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Count(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.Host == "127.0.0.1" && uri.Port == 0);

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
            if (addresses is null) return 0;
            // Kestrel publishes each address as it binds it, in the order Program.cs configures them: ListenUrl,
            // then the relay. Until the relay is bound as well, the last candidate can still be a dynamic API
            // port, so nothing is cached before then. Kestrel's enumerator is a snapshot of the live list.
            var candidates = new List<int>();
            foreach (var bound in addresses)
            {
                if (bound.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) && Uri.TryCreate(bound, UriKind.Absolute, out var uri) &&
                    uri.Port > 0 && !_apiPorts.Contains(uri.Port)) candidates.Add(uri.Port);
            }
            if (candidates.Count <= _dynamicApiListeners) return 0;
            Volatile.Write(ref _port, candidates[^1]);
            return _port;
        }
    }

    /// <summary>
    /// True for a request that arrived through the relay endpoint. The relay listens on 127.0.0.1 only;
    /// another loopback address (an API listener on 127.0.0.2) can use the same port number.
    /// </summary>
    public bool IsRelayed(HttpContext http) =>
        Port is > 0 and var port && http.Connection.LocalPort == port && !http.Request.IsHttps &&
        http.Connection.LocalIpAddress is { } local && (local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local).Equals(IPAddress.Loopback);

    /// <summary>The only paths the relay endpoint answers: the phone pages and the routes they call.</summary>
    public static bool Allowed(PathString path) =>
        path.StartsWithSegments("/vault") ||
        path.StartsWithSegments("/api/v1/vault/approvals") ||
        path.StartsWithSegments("/api/v1/vault/files") ||
        path.Equals("/api/v1/vault/device", StringComparison.Ordinal);
}
