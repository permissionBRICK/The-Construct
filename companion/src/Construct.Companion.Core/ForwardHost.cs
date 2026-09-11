using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Construct.Companion.Core;

public static partial class ForwardHost
{
    public static string Normalize(string? value)
    {
        var text = Space().Replace(Control().Replace(value ?? "", " "), " ").Trim(' ');
        if (text.Length > 200) text = text[..200].Trim(' ');
        var raw = Space().Replace(text, "");
        var bare = raw.Length > 2 && raw[0] == '[' && raw[^1] == ']' ? raw[1..^1] : raw;
        if (bare.Contains('%')) return "";
        if (bare.Contains(':')) return IsIpv6(bare) ? bare : "";
        return bare.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ? bare : "";
    }

    public static string ForUrl(string? value)
    {
        var bare = Normalize(value);
        return bare.Contains(':') ? $"[{bare}]" : bare;
    }
    public static string BindHostFor(string? value) => Normalize(value).Length == 0 ? "127.0.0.1" : "0.0.0.0";

    internal static bool IsIpv6(string value) => !value.Contains('%') && !value.Contains('[') && !value.Contains(']')
        && IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;

    internal static string TrimWhitespace(string value) => EdgeSpace().Replace(value, "");
    [GeneratedRegex(@"^[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]+|[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]+$")]
    private static partial Regex EdgeSpace();

    // ECMAScript whitespace, deliberately excluding .NET-only U+0085.
    [GeneratedRegex(@"[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]+")]
    private static partial Regex Space();
    [GeneratedRegex(@"[\u0000-\u001F\u007F\u2028\u2029]")]
    private static partial Regex Control();
}
