using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Construct.Agent.Protection;

/// <summary>
/// Protects the device's secrets at rest (the Companion's <c>IDataProtection</c>, 11.1): the device key,
/// the store token and the engine tokens are kept in the state file only in this protected form.
/// </summary>
public interface IDataProtection
{
    /// <summary><c>libsecret</c>, <c>file</c> (or <c>dpapi</c> in the Windows shell).</summary>
    string Name { get; }

    /// <summary>True when the protection is no stronger than the file permissions.</summary>
    bool Weak { get; }

    byte[] Protect(ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(ReadOnlySpan<byte> protectedBytes);
}

/// <summary>AES-256-GCM under one 256-bit protection key; where that key lives makes the difference.</summary>
public sealed class KeyedProtection(string name, bool weak, byte[] key) : IDataProtection
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Context = Encoding.UTF8.GetBytes("construct-agent-protect-v1");

    public string Name { get; } = name;

    public bool Weak { get; } = weak;

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var output = new byte[1 + NonceSize + plaintext.Length + TagSize];
        output[0] = 1;
        RandomNumberGenerator.Fill(output.AsSpan(1, NonceSize));
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(output.AsSpan(1, NonceSize), plaintext, output.AsSpan(1 + NonceSize, plaintext.Length), output.AsSpan(output.Length - TagSize), Context);
        return output;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
    {
        if (protectedBytes.Length < 1 + NonceSize + TagSize || protectedBytes[0] != 1) throw new CryptographicException("Not a protected value.");
        var length = protectedBytes.Length - 1 - NonceSize - TagSize;
        var plain = new byte[length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(protectedBytes.Slice(1, NonceSize), protectedBytes.Slice(1 + NonceSize, length), protectedBytes[^TagSize..], plain, Context);
        return plain;
    }
}

/// <summary>
/// The Linux protections. The protection key is kept in the user's keyring through libsecret
/// (<c>secret-tool</c>) when a session bus offers it; otherwise in a 0600 file next to the state,
/// which is the weaker choice: anyone who can read the user's files holds the device then.
/// The choice is recorded in the state at first run and kept, so a later start without a session
/// bus fails plainly instead of creating a second identity.
/// </summary>
public static class LinuxProtection
{
    public const string Libsecret = "libsecret";
    public const string File = "file";
    public const string KeyFileName = "protection.key";

    /// <param name="recorded">The protection the state names, or null on first run.</param>
    /// <param name="preferred">An explicit choice for first run (tests), or null for automatic.</param>
    public static IDataProtection Open(string dataDirectory, string? recorded, string? preferred)
    {
        var choice = recorded ?? preferred ?? (LibsecretAvailable() ? Libsecret : File);
        return choice switch
        {
            Libsecret => new KeyedProtection(Libsecret, false, LibsecretKey(dataDirectory)),
            File => new KeyedProtection(File, true, FileKey(dataDirectory)),
            _ => throw new InvalidOperationException($"Unknown protection '{choice}'."),
        };
    }

    private static byte[] FileKey(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, KeyFileName);
        if (System.IO.File.Exists(path)) return Convert.FromBase64String(System.IO.File.ReadAllText(path).Trim());
        var key = RandomNumberGenerator.GetBytes(32);
        PrivateFiles.Write(path, Encoding.ASCII.GetBytes(Convert.ToBase64String(key)));
        return key;
    }

    private static bool LibsecretAvailable()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))) return false;
        try
        {
            // A lookup that runs at all (found or not) means the secret service answers.
            var (code, _) = SecretTool(["lookup", "application", "construct-agent", "probe", "1"], null);
            return code is 0 or 1;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException)
        {
            return false;
        }
    }

    private static byte[] LibsecretKey(string dataDirectory)
    {
        string[] attributes = ["application", "construct-agent", "directory", Path.GetFullPath(dataDirectory)];
        var (code, output) = SecretTool(["lookup", .. attributes], null);
        if (code == 0 && output.Trim().Length > 0) return Convert.FromBase64String(output.Trim());

        var key = RandomNumberGenerator.GetBytes(32);
        var (stored, _) = SecretTool(["store", "--label=Construct device agent", .. attributes], Convert.ToBase64String(key));
        if (stored != 0) throw new InvalidOperationException("The keyring did not store the agent's protection key (secret-tool store failed).");
        return key;
    }

    private static (int Code, string Output) SecretTool(string[] arguments, string? input)
    {
        var start = new ProcessStartInfo("secret-tool") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("secret-tool did not start.");
        if (input is not null) process.StandardInput.Write(input);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("secret-tool did not answer.");
        }

        return (process.ExitCode, output.Result);
    }
}
