using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Constructd.Core.Logic;

/// <summary>
/// Shape of a backup's keys file (docs/plans/host-config-backups.md §3.2). The host stores it for the
/// clients and cannot use it: every recipient's copy of the backup's age identity is wrapped to that
/// recipient's RSA key.
/// <code>{"format":"construct-backup-keys/1","backupId":"&lt;id&gt;","createdAt":"&lt;ISO-8601 UTC&gt;",
///  "recipients":[{"fingerprint":"&lt;64 hex&gt;","label":"&lt;label&gt;","wrapped":"&lt;base64&gt;"}]}</code>
/// </summary>
public static partial class BackupKeysRules
{
    public const string Format = "construct-backup-keys/1";
    public const int MaxBytes = 64 * 1024;
    public const int MaxRecipients = 32;

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    public static partial Regex IdPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintPattern();

    [GeneratedRegex("^[A-Za-z0-9._ -]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();

    /// <summary>Null when <paramref name="json"/> is a valid keys file for backup <paramref name="backupId"/>; else the field and the reason.</summary>
    public static (string Field, string Reason)? Validate(ReadOnlySpan<byte> json, string backupId)
    {
        if (json.Length > MaxBytes) return ("keys", $"The keys file is larger than {MaxBytes} bytes.");
        JsonDocument document;
        try { document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException) { return ("keys", "Expected a JSON object."); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ("keys", "Expected a JSON object.");
            if (Text(root, "format") != Format) return ("format", $"Expected '{Format}'.");
            if (Text(root, "backupId") != backupId) return ("backupId", "Must be the id of this backup.");
            if (Text(root, "createdAt") is not { } created ||
                !DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                return ("createdAt", "Expected an ISO-8601 timestamp.");
            if (!root.TryGetProperty("recipients", out var recipients) || recipients.ValueKind != JsonValueKind.Array ||
                recipients.GetArrayLength() is < 1 or > MaxRecipients)
                return ("recipients", $"Expected 1 to {MaxRecipients} recipients.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var recipient in recipients.EnumerateArray())
            {
                if (recipient.ValueKind != JsonValueKind.Object) return ("recipients", "Each recipient must be an object.");
                if (Text(recipient, "fingerprint") is not { } fingerprint || !FingerprintPattern().IsMatch(fingerprint))
                    return ("recipients.fingerprint", "Expected 64 lowercase hex digits.");
                if (!seen.Add(fingerprint)) return ("recipients.fingerprint", "A recipient is listed twice.");
                if (Text(recipient, "label") is not { } label || !LabelPattern().IsMatch(label))
                    return ("recipients.label", "Expected 1 to 64 letters, digits, spaces, dots, underscores or hyphens.");
                if (Text(recipient, "wrapped") is not { Length: > 0 } wrapped || !IsBase64(wrapped))
                    return ("recipients.wrapped", "Expected base64.");
            }
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsBase64(string value)
    {
        var buffer = new byte[(value.Length + 3) / 4 * 3];
        return Convert.TryFromBase64String(value, buffer, out var written) && written > 0;
    }
}
