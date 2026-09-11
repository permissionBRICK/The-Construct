using System.Runtime.Versioning;
using System.Security.Cryptography;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Windows;

[SupportedOSPlatform("windows")]
public sealed class DpapiProtection : IDataProtection
{
    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var bytes=plaintext.ToArray();
        try { return ProtectedData.Protect(bytes,null,DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => ProtectedData.Unprotect(ciphertext.ToArray(), null, DataProtectionScope.CurrentUser);
}
