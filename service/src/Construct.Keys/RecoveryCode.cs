using System.Security.Cryptography;
using System.Text;

namespace Construct.Keys;

/// <summary>
/// The recovery code (8.5): 160 random bits, shown as 32 Crockford base32 characters in eight groups
/// of four. Two things are derived from it with HKDF-SHA256: the recovery secret, of which the store
/// keeps only a verifier, and the recovery private key (P-256), to whose public half <c>U</c> is
/// sealed. 160 bits make a fast verifier safe against guessing; the store rate-limits attempts anyway.
/// </summary>
public sealed class RecoveryCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int Size = 20;
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("construct-recovery-v1");

    private readonly byte[] _bits;

    private RecoveryCode(byte[] bits) => _bits = bits;

    public static RecoveryCode Generate() => new(RandomNumberGenerator.GetBytes(Size));

    /// <summary>Reads a code as the user typed it: case, spaces and dashes do not matter; O reads as 0, I and L as 1.</summary>
    public static RecoveryCode Parse(string text)
    {
        var clean = new StringBuilder();
        foreach (var c in text.ToUpperInvariant())
        {
            if (c is ' ' or '-') continue;
            clean.Append(c switch { 'O' => '0', 'I' or 'L' => '1', _ => c });
        }

        if (clean.Length != 32) throw new FormatException("A recovery code has 32 characters.");
        var bits = new byte[Size];
        int buffer = 0, count = 0, index = 0;
        foreach (var c in clean.ToString())
        {
            var value = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0) throw new FormatException($"'{c}' is not part of a recovery code.");
            buffer = (buffer << 5) | value;
            count += 5;
            if (count >= 8)
            {
                count -= 8;
                bits[index++] = (byte)(buffer >> count);
                buffer &= (1 << count) - 1;
            }
        }

        return new RecoveryCode(bits);
    }

    /// <summary>The code in groups: <c>XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX</c>.</summary>
    public override string ToString()
    {
        var chars = new StringBuilder();
        int buffer = 0, count = 0;
        foreach (var b in _bits)
        {
            buffer = (buffer << 8) | b;
            count += 8;
            while (count >= 5)
            {
                count -= 5;
                chars.Append(Alphabet[(buffer >> count) & 31]);
            }

            buffer &= (1 << count) - 1;
        }

        return string.Join('-', Enumerable.Range(0, 8).Select(i => chars.ToString(i * 4, 4)));
    }

    /// <summary>The recovery secret the device proves to the store.</summary>
    public byte[] Secret() => HKDF.DeriveKey(HashAlgorithmName.SHA256, _bits, 32, Salt, Encoding.UTF8.GetBytes("secret"));

    /// <summary>What the store keeps of a recovery secret.</summary>
    public static byte[] Verifier(byte[] secret) =>
        SHA256.HashData([.. Encoding.UTF8.GetBytes("construct-recovery-verifier-v1\n"), .. secret]);

    /// <summary>The recovery public key: the uncompressed P-256 point <c>U</c> is sealed to.</summary>
    public byte[] PublicKey()
    {
        using var key = PrivateKey();
        var point = new byte[65];
        ParticipantPublicKey.WritePoint(key.ExportParameters(false).Q, point);
        return point;
    }

    public RootSeal SealRoot(AccountKeys keys, string account) =>
        new(keys.Epoch, SealedBox.Seal(PublicKey(), keys.Root, Context(account, keys.Epoch)));

    /// <summary><c>U</c> from the recovery wrap; throws when this is not the code it was sealed for.</summary>
    public byte[] OpenRoot(RootSeal seal, string account)
    {
        using var key = PrivateKey();
        return SealedBox.Open(key, seal.Sealed, Context(account, seal.Epoch));
    }

    /// <summary>
    /// The P-256 scalar from HKDF. A 256-bit value outside the curve's order is rejected on import
    /// (probability about 2^-32); the next counter value is used then.
    /// </summary>
    private ECDiffieHellman PrivateKey()
    {
        for (var counter = 0; ; counter++)
        {
            var d = HKDF.DeriveKey(HashAlgorithmName.SHA256, _bits, 32, Salt, Encoding.UTF8.GetBytes($"key/{counter}"));
            try
            {
                return ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d });
            }
            catch (CryptographicException) when (counter < 16)
            {
            }
            finally
            {
                CryptographicOperations.ZeroMemory(d);
            }
        }
    }

    private static string Context(string account, long epoch) => $"root/recovery/{account}/{epoch}";
}
