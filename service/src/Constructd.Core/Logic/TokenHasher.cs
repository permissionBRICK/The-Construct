using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Constructd.Core.Logic;

/// <summary>
/// Token secrets and their storage form. Secrets are 256 bits of CSPRNG output, so a plain SHA-256
/// (no salt/KDF) is the right storage form: there is nothing to brute-force, and lookup by hash
/// stays a single indexed read.
/// </summary>
public static class TokenHasher
{
    private const int SecretBytes = 32;

    /// <summary>A new URL-safe token secret (base64url, unpadded).</summary>
    public static string GenerateSecret()
    {
        Span<byte> bytes = stackalloc byte[SecretBytes];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>Lowercase hex SHA-256 of a secret — the only form that is ever stored.</summary>
    public static string Hash(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Fixed-time comparison of two hashes.</summary>
    public static bool HashesEqual(string? left, string? right)
    {
        if (left is null || right is null || left.Length != right.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));
    }
}
