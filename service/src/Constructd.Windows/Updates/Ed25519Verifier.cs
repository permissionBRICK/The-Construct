using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
namespace Constructd.Windows.Updates;

public static class Ed25519Verifier
{
    public static bool Verify(byte[] manifest, byte[] signature, string? publicKey)
    {
        try
        {
            var key = Convert.FromBase64String(publicKey ?? "");
            if (key.Length != 32 || signature.Length != 64) return false;
            var signer = new Ed25519Signer();
            signer.Init(false, new Ed25519PublicKeyParameters(key, 0));
            signer.BlockUpdate(manifest, 0, manifest.Length);
            return signer.VerifySignature(signature);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { return false; }
    }
}
