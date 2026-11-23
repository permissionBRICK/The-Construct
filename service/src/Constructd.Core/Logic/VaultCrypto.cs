using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Construct.Keys;

namespace Constructd.Core.Logic;

/// <summary>
/// The vault's AES-256-GCM formats (docs/plans/key-vault-hosted.md, "Key material"); the sealing itself
/// is <see cref="VaultSeal"/>, which the device agent shares. Every sealed value
/// is <c>base64(nonce[12] || tag[16] || ciphertext)</c>. Entry payloads use the AAD
/// <c>entry:&lt;name&gt;:&lt;updatedAt&gt;</c> and the plaintext <c>{"username":"…","secret":"…","autoApprove":bool}</c>
/// (payloads sealed before auto-approve existed have no <c>autoApprove</c>: false);
/// the key check is <c>"construct-vault-check"</c> under the AAD <c>check</c>.
/// </summary>
public static class VaultCrypto
{
    public const int KeyBytes = 32, NonceBytes = 12, TagBytes = 16;
    public const string CheckText = "construct-vault-check", CheckAad = "check";

    public static string EntryAad(string name, long updatedAt) => VaultSeal.EntryAad(name, updatedAt);

    public static string Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, string aad) => VaultSeal.Seal(key, plaintext, aad);

    /// <summary>Throws <see cref="CryptographicException"/> for a wrong key, a tampered value or a wrong AAD.</summary>
    public static byte[] Open(ReadOnlySpan<byte> key, string sealedValue, string aad) => VaultSeal.Open(key, sealedValue, aad);

    public static string KeyCheck(ReadOnlySpan<byte> key) => Seal(key, Encoding.UTF8.GetBytes(CheckText), CheckAad);

    /// <summary>True when <paramref name="keyCheck"/> was sealed with <paramref name="key"/>.</summary>
    public static bool Verify(ReadOnlySpan<byte> key, string keyCheck)
    {
        if (key.Length != KeyBytes) return false;
        try
        {
            var plain = Open(key, keyCheck, CheckAad);
            return CryptographicOperations.FixedTimeEquals(plain, Encoding.UTF8.GetBytes(CheckText));
        }
        catch (CryptographicException) { return false; }
    }

    public static string SealEntry(ReadOnlySpan<byte> key, string name, long updatedAt, string username, string secret, bool autoApprove = false) =>
        VaultSeal.SealEntry(key, name, updatedAt, username, secret, autoApprove);

    public static (string Username, string Secret) OpenEntry(ReadOnlySpan<byte> key, string name, long updatedAt, string payload)
    {
        var (username, secret, _) = VaultSeal.OpenEntry(key, name, updatedAt, payload);
        return (username, secret);
    }

    /// <summary>Decodes a base64 key; null unless it is exactly 32 bytes.</summary>
    public static byte[]? DecodeKey(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length == KeyBytes) return bytes;
            CryptographicOperations.ZeroMemory(bytes);
            return null;
        }
        catch (FormatException) { return null; }
    }
}
