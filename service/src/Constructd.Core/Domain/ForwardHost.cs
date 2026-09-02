using System.Net;
using System.Net.Sockets;

namespace Constructd.Core.Domain;

/// <summary>
/// THE ONE HOST RULE BEHIND EVERY FORWARD LINK — shared with the extension's
/// <c>sanitizeHostLabel</c>/<c>urlHostFor</c> (extension/src/forwarder.js) and the guest CLI's
/// <c>wire_host_label</c>/<c>url_host</c> (bin/construct-expose.sh), and specified once in
/// <c>docs/expose.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// An IPv6 host label travels as the BARE literal — <c>fe80::1</c>, never <c>[fe80::1]</c> — and the
/// brackets are added exactly once, at the moment a URL is built. Without one representation and one
/// bracketing place the two modes disagreed: the guest CLI bracketed anything containing a colon (so
/// an accepted <c>[fe80::1]</c> became <c>http://[[fe80::1]]:5173/</c>) while this service
/// interpolated the label as it stood (so a bare <c>fe80::1</c> became <c>http://fe80::1:5173/</c>),
/// and neither link opens.
/// </para>
/// <para>
/// A zone id (<c>fe80::1%eth0</c>) is refused rather than percent-encoded: a URL would need it as
/// <c>%25</c>, and a zone is meaningful only on the machine that owns that interface — the opposite
/// of what a label advertising a PC to other machines is for.
/// </para>
/// </remarks>
public static class ForwardHost
{
    /// <summary>
    /// The canonical wire form of a host label: trimmed, unbracketed, and <c>null</c> when it is
    /// neither a usable host name nor a real IPv6 literal. A bracketed literal is accepted and
    /// unwrapped — a user typing what a browser shows is not an error — so both spellings are
    /// stored, echoed and rendered identically.
    /// </summary>
    public static string? Normalize(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var value = label.Trim();
        if (value.Length > 2 && value[0] == '[' && value[^1] == ']')
        {
            value = value[1..^1];
        }

        if (value.Length == 0)
        {
            return null;
        }

        if (value.Contains(':', StringComparison.Ordinal))
        {
            // A zone id is refused before parsing, not after: what .NET makes of "%eth0" is
            // platform-dependent, and either way it is not a wire host label. A REMAINING bracket
            // is refused for the same reason — .NET's TryParse accepts "[fe80::1]" and node's
            // isIP does not, so "[[fe80::1]]" would normalize to a bracketed value here and to
            // nothing at all in the extension. The wire form is decided in this rule, not by
            // whichever parser happens to be reading.
            if (value.Contains('%', StringComparison.Ordinal)
                || value.Contains('[', StringComparison.Ordinal)
                || value.Contains(']', StringComparison.Ordinal))
            {
                return null;
            }

            // Parsed, not character-classed: "::::" and "1::2::3" pass any plausible class and are
            // not addresses.
            return IPAddress.TryParse(value, out var parsed)
                && parsed.AddressFamily == AddressFamily.InterNetworkV6
                    ? value
                    : null;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-' && c != '_')
            {
                return null;
            }
        }

        return value;
    }

    /// <summary>
    /// The host as it goes INTO a URL: the canonical form with exactly one bracket pair around an
    /// IPv6 literal and nothing around anything else. <c>null</c> for a label that cannot be used,
    /// so the caller falls back to loopback the way an absent label does.
    /// </summary>
    public static string? ForUrl(string? label)
    {
        var bare = Normalize(label);
        if (bare is null)
        {
            return null;
        }

        return bare.Contains(':', StringComparison.Ordinal) ? $"[{bare}]" : bare;
    }
}
