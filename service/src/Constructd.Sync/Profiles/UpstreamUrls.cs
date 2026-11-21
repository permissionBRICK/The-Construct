namespace Constructd.Sync.Profiles;

/// <summary>
/// The rules for an upstream config repo's URL (docs/config-sync.md 7): it never carries a password (a bare user name is
/// fine: it tells the credential helper which credential to use), and it is shown only redacted.
/// </summary>
public static class UpstreamUrls
{
    /// <summary>True when the URL is no usable git remote here: empty, an option, or one carrying a password in its user info.</summary>
    public static bool Refused(string? url)
    {
        var text = url?.Trim() ?? "";
        if (text.Length is 0 or > 2048 || text.StartsWith('-') || text.Any(char.IsControl)) return true;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.UserInfo.Contains(':');
    }

    /// <summary>The URL without its user info, for messages and screens.</summary>
    public static string Display(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0
            ? new UriBuilder(uri) { UserName = "***", Password = "" }.Uri.ToString()
            : url;
}
