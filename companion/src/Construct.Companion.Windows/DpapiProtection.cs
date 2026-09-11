using System.Runtime.Versioning;
using System.Security.Cryptography;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Windows;

[SupportedOSPlatform("windows")]
public sealed class DpapiProtection : IDataProtection
{
    public byte[] Protect(ReadOnlySpan<byte> plaintext) => ProtectedData.Protect(plaintext.ToArray(), null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => ProtectedData.Unprotect(ciphertext.ToArray(), null, DataProtectionScope.CurrentUser);
}
