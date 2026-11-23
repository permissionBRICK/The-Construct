using System.Security.Cryptography;
using System.Text;

namespace Construct.Keys;

/// <summary>
/// Sealing to a participant's public key: ephemeral ECDH P-256 with the recipient's agreement key,
/// HKDF-SHA256 over the shared secret (salt: both points), AES-256-GCM. The context string names what
/// is sealed (and its epoch) and is bound as associated data, so a sealed value cannot be passed off
/// as another. Layout: version 1, ephemeral point (65), nonce (12), ciphertext, tag (16).
/// </summary>
public static class SealedBox
{
    private const int PointSize = 65;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Overhead = 1 + PointSize + NonceSize + TagSize;

    public static byte[] Seal(ParticipantPublicKey recipient, ReadOnlySpan<byte> plaintext, string context) =>
        Seal(recipient.AgreementPoint, plaintext, context);

    /// <param name="recipientPoint">The recipient's uncompressed P-256 agreement point.</param>
    public static byte[] Seal(ReadOnlySpan<byte> recipientPoint, ReadOnlySpan<byte> plaintext, string context)
    {
        using var recipient = ImportPoint(recipientPoint);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralPoint = new byte[PointSize];
        ParticipantPublicKey.WritePoint(ephemeral.ExportParameters(false).Q, ephemeralPoint);

        var info = Info(context);
        var key = DeriveKey(ephemeral.DeriveRawSecretAgreement(recipient.PublicKey), ephemeralPoint, recipientPoint, info);
        var output = new byte[Overhead + plaintext.Length];
        output[0] = 1;
        ephemeralPoint.CopyTo(output.AsSpan(1));
        var nonce = output.AsSpan(1 + PointSize, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(1 + PointSize + NonceSize, plaintext.Length), output.AsSpan(output.Length - TagSize), info);
        CryptographicOperations.ZeroMemory(key);
        return output;
    }

    /// <summary>Throws <see cref="CryptographicException"/> when the value was not sealed to this key under this context.</summary>
    public static byte[] Open(ECDiffieHellman own, byte[] sealedBytes, string context)
    {
        if (sealedBytes.Length < Overhead || sealedBytes[0] != 1) throw new CryptographicException("Not a sealed value.");
        var ephemeralPoint = sealedBytes.AsSpan(1, PointSize);
        using var ephemeral = ImportPoint(ephemeralPoint);
        var ownPoint = new byte[PointSize];
        ParticipantPublicKey.WritePoint(own.ExportParameters(false).Q, ownPoint);

        var info = Info(context);
        var key = DeriveKey(own.DeriveRawSecretAgreement(ephemeral.PublicKey), ephemeralPoint, ownPoint, info);
        var length = sealedBytes.Length - Overhead;
        var plain = new byte[length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(sealedBytes.AsSpan(1 + PointSize, NonceSize), sealedBytes.AsSpan(1 + PointSize + NonceSize, length),
            sealedBytes.AsSpan(sealedBytes.Length - TagSize), plain, info);
        CryptographicOperations.ZeroMemory(key);
        return plain;
    }

    internal static ECDiffieHellman ImportPoint(ReadOnlySpan<byte> point)
    {
        try
        {
            return ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = ParticipantPublicKey.ReadPoint(point) });
        }
        catch (FormatException ex)
        {
            throw new CryptographicException(ex.Message, ex);
        }
    }

    private static byte[] Info(string context) => Encoding.UTF8.GetBytes("construct-seal-v1\n" + context);

    private static byte[] DeriveKey(byte[] secret, ReadOnlySpan<byte> ephemeralPoint, ReadOnlySpan<byte> recipientPoint, byte[] info)
    {
        var salt = new byte[2 * PointSize];
        ephemeralPoint.CopyTo(salt);
        recipientPoint.CopyTo(salt.AsSpan(PointSize));
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, info);
        CryptographicOperations.ZeroMemory(secret);
        return key;
    }
}

/// <summary>
/// Wrapping under a 256-bit symmetric key (8.2: <c>wrap(U, K)</c> and friends): AES-256-GCM, the
/// context (key name and epoch) bound as associated data. Layout: version 1, nonce (12), ciphertext,
/// tag (16).
/// </summary>
public static class KeyWrap
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static byte[] Wrap(byte[] key, ReadOnlySpan<byte> plaintext, string context)
    {
        var output = new byte[1 + NonceSize + plaintext.Length + TagSize];
        output[0] = 1;
        var nonce = output.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(1 + NonceSize, plaintext.Length), output.AsSpan(output.Length - TagSize), Info(context));
        return output;
    }

    public static byte[] Unwrap(byte[] key, byte[] wrapped, string context)
    {
        if (wrapped.Length < 1 + NonceSize + TagSize || wrapped[0] != 1) throw new CryptographicException("Not a wrapped key.");
        var length = wrapped.Length - 1 - NonceSize - TagSize;
        var plain = new byte[length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(wrapped.AsSpan(1, NonceSize), wrapped.AsSpan(1 + NonceSize, length), wrapped.AsSpan(wrapped.Length - TagSize), plain, Info(context));
        return plain;
    }

    private static byte[] Info(string context) => Encoding.UTF8.GetBytes("construct-wrap-v1\n" + context);
}
