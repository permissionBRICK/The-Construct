using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Fakes;
public sealed class FakeHostConversionCrypto : IHostConversionCrypto
{
    public string Id { get; set; } = "0123456789abcdef0123456789abcdef";
    public Secret PrivateKey { get; set; } = new("fake-private-key");
    public Secret Token { get; set; } = new("fake-token");
    public int Decryptions { get; private set; }
    public ConversionKey CreateKey() => new(Id, "<RSAKeyValue><Modulus>AQ==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>", PrivateKey);
    public Secret Decrypt(Secret privateKey, string encryptedToken) { Decryptions++; return Token; }
}
