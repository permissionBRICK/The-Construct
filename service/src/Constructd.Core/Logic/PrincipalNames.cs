namespace Constructd.Core.Logic;

/// <summary>
/// One spelling for a signed-in Windows user, whichever host kind authenticated them. A Windows
/// host's Negotiate handler names the user <c>DOMAIN\user</c>; Kerberos through GSSAPI on a Linux
/// host names the same person <c>user@REALM</c>. The user store keys on the first form, so the second
/// is mapped onto it — with the NetBIOS domain name the administrator configured, because the realm
/// (<c>DC.EXAMPLE.NET</c>) does not reveal it.
/// </summary>
public static class PrincipalNames
{
    /// <summary>
    /// <c>user@REALM</c> → <c>DOMAIN\user</c> when <paramref name="domainName"/> is set and the realm
    /// matches <paramref name="realm"/> (or any realm when that is empty). Everything else — already
    /// <c>DOMAIN\user</c>, a bare token user name, an unmatched realm — is returned unchanged. Pure.
    /// </summary>
    public static string Normalize(string? name, string? domainName, string? realm)
    {
        var value = name?.Trim() ?? string.Empty;
        var domain = domainName?.Trim() ?? string.Empty;
        if (value.Length == 0 || domain.Length == 0 || value.Contains('\\'))
        {
            return value;
        }

        var at = value.LastIndexOf('@');
        if (at <= 0 || at == value.Length - 1)
        {
            return value;
        }

        var user = value[..at];
        var principalRealm = value[(at + 1)..];
        var expected = realm?.Trim() ?? string.Empty;
        if (expected.Length > 0 && !string.Equals(expected, principalRealm, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return $"{domain}\\{user}";
    }
}
