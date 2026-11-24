namespace Constructd.Core.Logic;

/// <summary>
/// Normalizes <c>Constructd:ListenUrl</c> entries for Kestrel. Wildcard <c>0.0.0.0</c> binds dual-stack
/// (<c>+</c>, Kestrel's <c>ListenAnyIP</c>) so the API answers on both IPv6 and IPv4 wherever the OS supports
/// IPv6, and falls back to IPv4 automatically when IPv6 is disabled. Specific interface and loopback addresses
/// (such as <c>127.0.0.1</c> or a single LAN IP) are preserved verbatim.
/// </summary>
public static class ListenUrls
{
    public static string Normalize(string listenUrl)
    {
        ArgumentNullException.ThrowIfNull(listenUrl);
        var parts = listenUrl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = NormalizeSingle(parts[i]);
        }

        return string.Join(';', parts);
    }

    private static string NormalizeSingle(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (!string.Equals(uri.Host, "0.0.0.0", StringComparison.Ordinal)) return url;
        if (uri.Scheme is not ("http" or "https")) return url;
        if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0) return url;
        return $"{uri.Scheme}://+:{uri.Port}";
    }
}
