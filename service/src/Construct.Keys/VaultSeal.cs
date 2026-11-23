using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Construct.Keys;

/// <summary>
/// The vault's AES-256-GCM format under <c>K</c> (docs/plans/key-vault-hosted.md, "Key material"),
/// shared by the engine and the device agent: every sealed value is
/// <c>base64(nonce[12] || tag[16] || ciphertext)</c>. An entry's payload uses the AAD
/// <c>entry:&lt;name&gt;:&lt;updatedAt&gt;</c> and the plaintext <c>{"username","secret","autoApprove"}</c>.
/// </summary>
public static class VaultSeal
{
    public const int KeyBytes = 32, NonceBytes = 12, TagBytes = 16;

    public static string EntryAad(string name, long updatedAt) => "entry:" + name + ":" + updatedAt.ToString(CultureInfo.InvariantCulture);

    public static string Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, string aad)
    {
        RequireKey(key);
        var sealedBytes = new byte[NonceBytes + TagBytes + plaintext.Length];
        var nonce = sealedBytes.AsSpan(0, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, sealedBytes.AsSpan(NonceBytes + TagBytes), sealedBytes.AsSpan(NonceBytes, TagBytes), Encoding.UTF8.GetBytes(aad));
        return Convert.ToBase64String(sealedBytes);
    }

    /// <summary>Throws <see cref="CryptographicException"/> for a wrong key, a tampered value or a wrong AAD.</summary>
    public static byte[] Open(ReadOnlySpan<byte> key, string sealedValue, string aad)
    {
        RequireKey(key);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(sealedValue); }
        catch (FormatException) { throw new CryptographicException("The sealed value is not base64."); }
        if (bytes.Length < NonceBytes + TagBytes) throw new CryptographicException("The sealed value is too short.");
        var plain = new byte[bytes.Length - NonceBytes - TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(bytes.AsSpan(0, NonceBytes), bytes.AsSpan(NonceBytes + TagBytes), bytes.AsSpan(NonceBytes, TagBytes), plain, Encoding.UTF8.GetBytes(aad));
        return plain;
    }

    public static string SealEntry(ReadOnlySpan<byte> key, string name, long updatedAt, string username, string secret, bool autoApprove = false)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(new EntryPlaintext(username, secret, autoApprove), EntryJson);
        try { return Seal(key, plain, EntryAad(name, updatedAt)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>The username, the value and the sealed auto-approve flag (false for payloads sealed before it existed).</summary>
    public static (string Username, string Secret, bool AutoApprove) OpenEntry(ReadOnlySpan<byte> key, string name, long updatedAt, string payload)
    {
        var plain = Open(key, payload, EntryAad(name, updatedAt));
        try
        {
            var value = JsonSerializer.Deserialize<EntryPlaintext>(plain, EntryJson) ?? throw new CryptographicException("The entry is empty.");
            return (value.Username ?? "", value.Secret ?? throw new CryptographicException("The entry has no secret."), value.AutoApprove);
        }
        catch (JsonException) { throw new CryptographicException("The entry is not valid JSON."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static void RequireKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyBytes) throw new CryptographicException("The vault key must be 32 bytes.");
    }

    private sealed record EntryPlaintext(string? Username, string? Secret, bool AutoApprove = false);

    private static readonly JsonSerializerOptions EntryJson = new(JsonSerializerDefaults.Web);
}
