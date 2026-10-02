using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
namespace Constructd.Windows.Media;

// The host master-key pattern: 32 random bytes in a protected directory, DPAPI LocalMachine on Windows
// and a root-only file elsewhere. Each use gets its own key file (windows-master.key, vault-master.key).
public sealed class WindowsKeyCipher(string directory, string fileName = "windows-master.key")
{
    private byte[]? master;
    private readonly object sync = new();
    // The random host master survives service restarts and identifies the owning host.
    public string HostId => Convert.ToHexStringLower(SHA256.HashData(Master()));
    private byte[] Master()
    {
        lock (sync)
        {
            if (master is not null) return master;
            Directory.CreateDirectory(directory);
            if (OperatingSystem.IsWindows())
            {
                var acl = new DirectorySecurity();
                acl.SetAccessRuleProtection(true, false);
                foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                    acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(directory).SetAccessControl(acl);
            }
            else File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
            {
                var generated = RandomNumberGenerator.GetBytes(32);
                var stored = OperatingSystem.IsWindows() ? ProtectedData.Protect(generated, null, DataProtectionScope.LocalMachine) : generated;
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var file = new FileStream(path, options);
                file.Write(stored); file.Flush(true);
                CryptographicOperations.ZeroMemory(generated);
            }
            var bytes = File.ReadAllBytes(path);
            master = OperatingSystem.IsWindows() ? ProtectedData.Unprotect(bytes, null, DataProtectionScope.LocalMachine) : bytes;
            if (master.Length != 32) throw new CryptographicException("Invalid Windows key protection material.");
            return master;
        }
    }
    public string Encrypt(string value)
    {
        var plain = Encoding.UTF8.GetBytes(value);
        try { return EncryptBytes(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public string Decrypt(string value)
    {
        var plain = DecryptBytes(value);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public string EncryptBytes(ReadOnlySpan<byte> plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[plain.Length]; var tag = new byte[16];
        using var aes = new AesGcm(Master(), 16); aes.Encrypt(nonce, plain, cipher, tag); return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }
    // The caller owns (and zeroes) the returned plaintext.
    public byte[] DecryptBytes(string value)
    {
        var bytes = Convert.FromBase64String(value); if (bytes.Length < 28) throw new CryptographicException();
        var plain = new byte[bytes.Length - 28];
        try { using var aes = new AesGcm(Master(), 16); aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain); return plain; }
        catch { CryptographicOperations.ZeroMemory(plain); throw; }
    }
}

/// <summary>Wraps a user's vault key with the host's own vault master key (vault-master.key).</summary>
public sealed class HostVaultKeyProtector(WindowsKeyCipher cipher) : Constructd.Core.Abstractions.IVaultKeyProtector
{
    public const string FileName = "vault-master.key";
    public string Wrap(ReadOnlySpan<byte> key) => cipher.EncryptBytes(key);
    public byte[] Unwrap(string wrapped)
    {
        try { return cipher.DecryptBytes(wrapped); }
        catch (FormatException) { throw new CryptographicException("The wrapped vault key is not base64."); }
    }
}
