using System.Security.Cryptography;
using System.Text;
namespace Constructd.Windows.Media;

public sealed class WindowsKeyCipher(string directory)
{
    private byte[]? master;
    private readonly object sync = new();
    private byte[] Master()
    {
        lock (sync)
        {
            if (master is not null) return master;
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var path = Path.Combine(directory, "windows-master.key");
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
        var nonce = RandomNumberGenerator.GetBytes(12); var plain = Encoding.UTF8.GetBytes(value); var cipher = new byte[plain.Length]; var tag = new byte[16];
        try { using var aes = new AesGcm(Master(), 16); aes.Encrypt(nonce, plain, cipher, tag); return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray()); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public string Decrypt(string value)
    {
        var bytes = Convert.FromBase64String(value); if (bytes.Length < 28) throw new CryptographicException();
        var plain = new byte[bytes.Length - 28];
        try { using var aes = new AesGcm(Master(), 16); aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain); return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
