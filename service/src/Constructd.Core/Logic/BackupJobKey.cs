using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Constructd.Core.Logic;

/// <summary>
/// The key a reinstall job opens its own pre-reinstall backup with (design 10.2, S29). The engine
/// makes an RSA-3072 pair per job, the guest enrols the public half as one more recipient
/// (<c>construct backup recipients add</c>), and the private half unwraps the backup's key
/// <c>E</c> from the keys file the host stores (<c>construct-backup-keys/1</c>, RSA-OAEP-SHA256, as
/// <c>bin/construct-backup.sh</c> wraps it). The private half is a PKCS#8 PEM string that lives in the
/// job's secret bag only.
/// </summary>
public static partial class BackupJobKey
{
    /// <summary>The recipient label on the guest (<c>LABEL_RE</c> of construct-backup.sh).</summary>
    public const string Label = "Construct reinstall job";

    [GeneratedRegex("^AGE-SECRET-KEY-1[0-9A-Z]+$")]
    private static partial Regex IdentityPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex FingerprintPattern();

    /// <summary>A backup key as the guest reads it from stdin.</summary>
    public static bool IsIdentity(string? value) => value is not null && IdentityPattern().IsMatch(value);

    public static bool IsFingerprint(string? value) => value is not null && FingerprintPattern().IsMatch(value);

    /// <summary>A new pair: the private half as PKCS#8 PEM, the public half as SubjectPublicKeyInfo PEM, and its fingerprint.</summary>
    public static (string PrivatePem, string PublicPem, string Fingerprint) Create()
    {
        using var rsa = RSA.Create(3072);
        var spki = rsa.ExportSubjectPublicKeyInfo();
        return (rsa.ExportPkcs8PrivateKeyPem(), PemEncoding.WriteString("PUBLIC KEY", spki), Fingerprint(spki));
    }

    /// <summary>Lowercase hex SHA-256 of the SubjectPublicKeyInfo DER, as the guest names a recipient.</summary>
    public static string Fingerprint(byte[] subjectPublicKeyInfo) => Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo));

    /// <summary>The fingerprint of a private key's public half.</summary>
    public static string FingerprintOf(string privatePem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privatePem);
        return Fingerprint(rsa.ExportSubjectPublicKeyInfo());
    }

    /// <summary>
    /// Unwraps <c>E</c> for <paramref name="backupId"/> from its keys file with the job's private key.
    /// Null when the keys file is not that backup's, names no recipient with this key, or does not
    /// unwrap to a backup key.
    /// </summary>
    public static string? Unwrap(string? keysJson, string backupId, string privatePem)
    {
        if (string.IsNullOrEmpty(keysJson)) return null;
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privatePem);
        return Construct.Keys.BackupKeyFile.Unwrap(keysJson, backupId, rsa);
    }

    /// <summary>Whether the keys file names a recipient besides <paramref name="jobFingerprint"/>.</summary>
    public static bool HasOtherRecipient(string? keysJson, string jobFingerprint)
    {
        if (string.IsNullOrEmpty(keysJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(keysJson);
            return doc.RootElement.TryGetProperty("recipients", out var recipients) && recipients.ValueKind == JsonValueKind.Array
                && recipients.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("fingerprint", out var fp)
                    && fp.ValueKind == JsonValueKind.String && fp.GetString() != jobFingerprint && r.TryGetProperty("wrapped", out _));
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Wraps <paramref name="identity"/> for a public key, as the guest does (tests stand in for the guest with it).</summary>
    public static string Wrap(string identity, string publicPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicPem);
        return Convert.ToBase64String(rsa.Encrypt(Encoding.ASCII.GetBytes(identity), RSAEncryptionPadding.OaepSHA256));
    }

    /// <summary>The fingerprint of a public key in PEM form, or null when it is not one.</summary>
    public static string? PublicFingerprint(string publicPem)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicPem);
            return Fingerprint(rsa.ExportSubjectPublicKeyInfo());
        }
        catch (ArgumentException) { return null; }
        catch (CryptographicException) { return null; }
    }
}
