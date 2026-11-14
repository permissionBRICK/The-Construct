namespace Construct.Companion.Core.Abstractions;

public interface IHostConversionCrypto
{
    ConversionKey CreateKey();
    Secret Decrypt(Secret privateKey, string encryptedToken);
}
public sealed record ConversionKey(string Id, string PublicKeyXml, Secret PrivateKey)
{
    public override string ToString() => "ConversionKey";
}
