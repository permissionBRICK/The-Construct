using System.Text.RegularExpressions;
using Constructd.Core.Configuration;

namespace Constructd.Core.Logic;

/// <summary>
/// Where phones open the key vault's approval and pairing pages (docs/key-vault.md, "Hosted VMs"). In order:
/// the host setting <c>vault.webUrl</c> (Host Administration, host admins only), the service option
/// <c>Constructd:VaultWebUrl</c>, and the service's own <c>https://&lt;PublicHost&gt;:&lt;port&gt;</c>, whose
/// certificate is self-signed. Agents and VM tokens cannot change any of them.
/// </summary>
public static partial class VaultWebAddress
{
    /// <summary>Where the address came from, as the device routes report it (<c>webUrlSource</c>).</summary>
    public const string FromHostConfig = "hostConfig", FromOption = "option", ServiceDefault = "default";

    public const string Rule = "vault.webUrl must be an https address without a path, query or fragment (http only for localhost), e.g. https://vault.example.net, or null for the host service's own address.";

    public static (string Url, string Source) Resolve(string? configured, ConstructdOptions options) =>
        !string.IsNullOrWhiteSpace(configured) ? (configured.Trim().TrimEnd('/'), FromHostConfig)
        : (options.VaultWebBase(), string.IsNullOrWhiteSpace(options.VaultWebUrl) ? ServiceDefault : FromOption);

    /// <summary>
    /// Null when the host setting is acceptable: null or blank (unset), or an absolute https origin with an
    /// optional port and at most a trailing slash. Plain http only for a loopback host (tests, a local proxy).
    /// </summary>
    public static string? Problem(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var value = url.Trim();
        if (!Origin().IsMatch(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return Rule;
        if (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) return null;
        return Rule;
    }

    [GeneratedRegex(@"^https?://(?:[A-Za-z0-9._-]+|\[[0-9A-Fa-f:.]+\])(?::(?:[1-9][0-9]{0,3}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5]))?/?$")]
    private static partial Regex Origin();
}
