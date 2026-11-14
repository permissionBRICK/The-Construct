using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Windows;

[SupportedOSPlatform("windows")]
public sealed class HostConversionCrypto : IHostConversionCrypto
{
    public ConversionKey CreateKey()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportParameters(false);
        return new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            "<RSAKeyValue><Modulus>" + Convert.ToBase64String(publicKey.Modulus!) + "</Modulus><Exponent>" + Convert.ToBase64String(publicKey.Exponent!) + "</Exponent></RSAKeyValue>",
            new Secret(rsa.ExportPkcs8PrivateKeyPem()));
    }
    public Secret Decrypt(Secret privateKey, string encryptedToken)
    {
        using var rsa = RSA.Create(); rsa.ImportFromPem(privateKey.Reveal());
        var decrypted = rsa.Decrypt(Convert.FromBase64String(encryptedToken), RSAEncryptionPadding.OaepSHA1);
        try { return new Secret(Encoding.UTF8.GetString(decrypted)); }
        finally { CryptographicOperations.ZeroMemory(decrypted); }
    }
}
