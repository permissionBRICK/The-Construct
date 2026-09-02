using System.Text;

namespace Constructd.Windows.HyperV;

/// <summary>
/// <c>powershell.exe -EncodedCommand</c> takes base64 of the script encoded as UTF-16LE. Encoding the
/// script instead of passing it inline is what makes quoting a non-issue: the whole script — newlines,
/// quotes and all — is one opaque argv element.
/// </summary>
public static class PowerShellEncoding
{
    public static string Encode(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    /// <summary>The inverse, so a test can assert what the service would actually run.</summary>
    public static string Decode(string encodedCommand)
    {
        ArgumentNullException.ThrowIfNull(encodedCommand);
        return Encoding.Unicode.GetString(Convert.FromBase64String(encodedCommand));
    }
}
