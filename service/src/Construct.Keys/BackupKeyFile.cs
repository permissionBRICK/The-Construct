using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Construct.Keys;

/// <summary>
/// A stored backup's keys file (<c>construct-backup-keys/1</c>, written by <c>bin/construct-backup.sh</c>):
/// the backup's age key <c>E</c> wrapped with RSA-OAEP-SHA256 for each recipient, named by the
/// SHA-256 of its SubjectPublicKeyInfo. A device opens it with <c>B</c> (8.2), a reinstall job with its own key.
/// </summary>
public static partial class BackupKeyFile
{
    [GeneratedRegex("^AGE-SECRET-KEY-1[0-9A-Z]+$")]
    private static partial Regex IdentityPattern();

    /// <summary>A backup key as the guest reads it from stdin.</summary>
    public static bool IsIdentity(string? value) => value is not null && IdentityPattern().IsMatch(value);

    /// <summary>Lowercase hex SHA-256 of the SubjectPublicKeyInfo DER, as the guest names a recipient.</summary>
    public static string Fingerprint(RSA key) => Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));

    /// <summary>
    /// <c>E</c> of <paramref name="backupId"/> unwrapped with <paramref name="key"/>; null when the file
    /// is not that backup's, names no recipient with this key, or does not unwrap to a backup key.
    /// </summary>
    public static string? Unwrap(string? keysJson, string backupId, RSA key)
    {
        if (string.IsNullOrEmpty(keysJson)) return null;
        var fingerprint = Fingerprint(key);
        try
        {
            using var doc = JsonDocument.Parse(keysJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != "construct-backup-keys/1"
                || !root.TryGetProperty("backupId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != backupId
                || !root.TryGetProperty("recipients", out var recipients) || recipients.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var recipient in recipients.EnumerateArray())
            {
                if (recipient.ValueKind != JsonValueKind.Object
                    || !recipient.TryGetProperty("fingerprint", out var fp) || fp.ValueKind != JsonValueKind.String || fp.GetString() != fingerprint
                    || !recipient.TryGetProperty("wrapped", out var wrapped) || wrapped.ValueKind != JsonValueKind.String || wrapped.GetString() is not { } text)
                    continue;
                var bytes = key.Decrypt(Convert.FromBase64String(text), RSAEncryptionPadding.OaepSHA256);
                try
                {
                    var plain = Encoding.ASCII.GetString(bytes);
                    return IsIdentity(plain) ? plain : null;
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or InvalidOperationException)
        {
            return null;
        }

        return null;
    }
}
