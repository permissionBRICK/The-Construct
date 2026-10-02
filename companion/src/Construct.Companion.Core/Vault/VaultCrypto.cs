using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Core.Vault;

// The vault key K and what it protects on a host (docs/plans/key-vault-hosted.md, "Key material"):
// AES-256-GCM, encoded base64(nonce[12] || tag[16] || ciphertext). An entry's AAD binds its name and
// updatedAt, so a payload moved to another name or replayed under another timestamp does not decrypt.
public static class VaultCrypto
{
    public const int KeyBytes = 32, NonceBytes = 12, TagBytes = 16;
    private const string CheckText = "construct-vault-check", CheckAad = "check";
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyBytes);
    public static string EntryAad(string name, long updatedAt) => $"entry:{name}:{updatedAt}";

    public static string Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, string aad)
    {
        var sealedBytes = new byte[NonceBytes + TagBytes + plaintext.Length];
        var nonce = sealedBytes.AsSpan(0, NonceBytes); RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, sealedBytes.AsSpan(NonceBytes + TagBytes), sealedBytes.AsSpan(NonceBytes, TagBytes), Encoding.UTF8.GetBytes(aad));
        return Convert.ToBase64String(sealedBytes);
    }
    // Null for anything that is not an intact payload sealed with this key and AAD.
    public static byte[]? Decrypt(ReadOnlySpan<byte> key, string? sealedText, string aad)
    {
        byte[] sealedBytes;
        try { sealedBytes = Convert.FromBase64String(sealedText ?? ""); }
        catch (FormatException) { return null; }
        if (sealedBytes.Length < NonceBytes + TagBytes || key.Length != KeyBytes) return null;
        var plaintext = new byte[sealedBytes.Length - NonceBytes - TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(sealedBytes.AsSpan(0, NonceBytes), sealedBytes.AsSpan(NonceBytes + TagBytes), sealedBytes.AsSpan(NonceBytes, TagBytes), plaintext, Encoding.UTF8.GetBytes(aad));
            return plaintext;
        }
        catch (CryptographicException) { CryptographicOperations.ZeroMemory(plaintext); return null; }
    }

    public static string KeyCheck(ReadOnlySpan<byte> key) => Encrypt(key, Encoding.UTF8.GetBytes(CheckText), CheckAad);
    public static bool MatchesKeyCheck(ReadOnlySpan<byte> key, string? keyCheck)
    {
        if (key.Length != KeyBytes || string.IsNullOrEmpty(keyCheck)) return false;
        var plain = Decrypt(key, keyCheck, CheckAad);
        return plain is not null && plain.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(CheckText));
    }

    // Plaintext {"username","secret"}; the buffer holding the JSON is cleared after use.
    public static string SealEntry(ReadOnlySpan<byte> key, string name, long updatedAt, string username, Secret value)
    {
        var plain = Encoding.UTF8.GetBytes(new JsonObject { ["username"] = username, ["secret"] = value.Reveal() }.ToJsonString());
        try { return Encrypt(key, plain, EntryAad(name, updatedAt)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static (string Username, Secret Value)? OpenEntry(ReadOnlySpan<byte> key, string name, long updatedAt, string? payload)
    {
        var plain = Decrypt(key, payload, EntryAad(name, updatedAt));
        if (plain is null) return null;
        try
        {
            if (Runtime.RuntimeJson.Parse(new UTF8Encoding(false, true).GetString(plain)) is not { } json
                || json["secret"] is not JsonValue s || !s.TryGetValue<string>(out var secret) || secret.Length == 0) return null;
            return (json["username"] is JsonValue u && u.TryGetValue<string>(out var username) ? username : "", new Secret(secret));
        }
        catch (DecoderFallbackException) { return null; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    // The key as the user copies it between PCs: standard base64 of the 32 bytes (url-safe input accepted).
    public static string Export(ReadOnlySpan<byte> key) => Convert.ToBase64String(key);
    public static byte[]? Import(string? text)
    {
        var clean = new string((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace('-', '+').Replace('_', '/');
        if (clean.Length % 4 != 0) clean = clean.PadRight(clean.Length + 4 - clean.Length % 4, '=');
        try { var key = Convert.FromBase64String(clean); return key.Length == KeyBytes ? key : null; }
        catch (FormatException) { return null; }
    }
}
