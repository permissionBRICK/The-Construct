using System.Security.Cryptography;
using System.Text;

namespace Construct.Keys;

/// <summary>
/// The key pair of a participant: a device (8.1) or an engine's host key (8.7). Two NIST P-256 keys,
/// one for ECDSA signatures (pairing grants, request proofs) and one for ECDH (things sealed to the
/// participant), so neither key is used for two purposes.
///
/// P-256 rather than Ed25519/X25519: .NET 10 has no Ed25519 or X25519 type, and P-256 ECDSA, ECDH
/// and AES-GCM are built in on Linux, Windows and macOS without a native package.
/// </summary>
public sealed class ParticipantKey : IDisposable
{
    private const string ExportPrefix = "p256v1:";

    private readonly ECDsa _signing;
    private readonly ECDiffieHellman _agreement;

    private ParticipantKey(ECDsa signing, ECDiffieHellman agreement)
    {
        _signing = signing;
        _agreement = agreement;
        PublicKey = ParticipantPublicKey.From(signing.ExportParameters(false).Q, agreement.ExportParameters(false).Q);
    }

    public ParticipantPublicKey PublicKey { get; }

    public static ParticipantKey Generate() =>
        new(ECDsa.Create(ECCurve.NamedCurves.nistP256), ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>
    /// The private halves as text, for the platform key store of a device or the engine's key file.
    /// Anyone holding this text is the participant.
    /// </summary>
    public string Export() =>
        ExportPrefix + Convert.ToBase64String(_signing.ExportPkcs8PrivateKey()) + "." + Convert.ToBase64String(_agreement.ExportPkcs8PrivateKey());

    public static ParticipantKey Import(string exported)
    {
        if (!exported.StartsWith(ExportPrefix, StringComparison.Ordinal)) throw new FormatException("Not an exported participant key.");
        var parts = exported[ExportPrefix.Length..].Split('.');
        if (parts.Length != 2) throw new FormatException("Not an exported participant key.");
        var signing = ECDsa.Create();
        signing.ImportPkcs8PrivateKey(Convert.FromBase64String(parts[0]), out _);
        var agreement = ECDiffieHellman.Create();
        agreement.ImportPkcs8PrivateKey(Convert.FromBase64String(parts[1]), out _);
        return new ParticipantKey(signing, agreement);
    }

    /// <summary>An ECDSA P-256 signature over SHA-256 of <paramref name="data"/>, IEEE P1363 format (64 bytes).</summary>
    public byte[] Sign(ReadOnlySpan<byte> data) =>
        _signing.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public byte[] Sign(string text) => Sign(Encoding.UTF8.GetBytes(text));

    /// <summary>Opens something <see cref="SealedBox.Seal"/> sealed to this participant.</summary>
    public byte[] Open(byte[] sealedBytes, string context) => SealedBox.Open(_agreement, sealedBytes, context);

    public void Dispose()
    {
        _signing.Dispose();
        _agreement.Dispose();
    }
}

/// <summary>
/// The public half of a <see cref="ParticipantKey"/>: version byte 1, the uncompressed signing point
/// and the uncompressed agreement point (131 bytes), written as unpadded base64url. The device id is
/// derived from it, so a device record cannot name one id and another key.
/// </summary>
public sealed class ParticipantPublicKey : IEquatable<ParticipantPublicKey>
{
    private const int PointSize = 65;
    private const int Size = 1 + 2 * PointSize;

    private readonly byte[] _bytes;

    private ParticipantPublicKey(byte[] bytes) => _bytes = bytes;

    internal static ParticipantPublicKey From(ECPoint signing, ECPoint agreement)
    {
        var bytes = new byte[Size];
        bytes[0] = 1;
        WritePoint(signing, bytes.AsSpan(1, PointSize));
        WritePoint(agreement, bytes.AsSpan(1 + PointSize, PointSize));
        return new ParticipantPublicKey(bytes);
    }

    public static ParticipantPublicKey Parse(string text)
    {
        var bytes = Base64Url.Decode(text);
        if (bytes.Length != Size || bytes[0] != 1) throw new FormatException("Not a participant public key.");
        var key = new ParticipantPublicKey(bytes);
        // Rejects points that are not on the curve.
        using (key.CreateVerifier()) { }
        using (key.CreateAgreement()) { }
        return key;
    }

    public static bool TryParse(string? text, out ParticipantPublicKey? key)
    {
        key = null;
        if (string.IsNullOrEmpty(text) || text.Length > 400) return false;
        try
        {
            key = Parse(text);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The participant's id: the first 20 hex digits of SHA-256 over the key bytes.</summary>
    public string Id => Convert.ToHexString(SHA256.HashData(_bytes))[..20].ToLowerInvariant();

    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>The uncompressed agreement point that things are sealed to.</summary>
    public ReadOnlySpan<byte> AgreementPoint => _bytes.AsSpan(1 + PointSize, PointSize);

    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        using var verifier = CreateVerifier();
        return verifier.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public bool Verify(string text, ReadOnlySpan<byte> signature) => Verify(Encoding.UTF8.GetBytes(text), signature);

    public override string ToString() => Base64Url.Encode(_bytes);

    public bool Equals(ParticipantPublicKey? other) => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => Equals(obj as ParticipantPublicKey);

    public override int GetHashCode() => BitConverter.ToInt32(SHA256.HashData(_bytes));

    private ECDsa CreateVerifier() => ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = ReadPoint(_bytes.AsSpan(1, PointSize)) });

    private ECDiffieHellman CreateAgreement() => SealedBox.ImportPoint(AgreementPoint);

    internal static ECPoint ReadPoint(ReadOnlySpan<byte> point)
    {
        if (point.Length != PointSize || point[0] != 4) throw new FormatException("Not an uncompressed P-256 point.");
        return new ECPoint { X = point.Slice(1, 32).ToArray(), Y = point.Slice(33, 32).ToArray() };
    }

    internal static void WritePoint(ECPoint point, Span<byte> target)
    {
        target[0] = 4;
        point.X!.CopyTo(target[1..]);
        point.Y!.CopyTo(target[33..]);
    }
}

/// <summary>Unpadded base64url, the text form of keys, tokens and signatures.</summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

    public static byte[] Decode(string text)
    {
        try
        {
            return System.Buffers.Text.Base64Url.DecodeFromChars(text);
        }
        catch (FormatException)
        {
            throw new FormatException("Not base64url.");
        }
    }
}
