using System.Security.Cryptography;
using System.Text;

namespace Constructd.Sync.Sealing;

/// <summary>
/// Where the sync key <c>D</c> (8.2) of an account and epoch comes from. Unwrapping it under
/// <c>U</c> or the host key is pairing work (8.3); this interface is the seam it plugs into.
/// </summary>
public interface IRecordKeyProvider
{
    /// <summary>The 256-bit key; throws when this participant does not hold it.</summary>
    Task<byte[]> GetKeyAsync(string account, long epoch, CancellationToken ct);
}

/// <summary>
/// One key file per account and epoch in a directory, created on first use. Good enough for tests
/// and a single-user install; every participant that shares the directory shares <c>D</c>.
/// </summary>
public sealed class FileRecordKeyProvider(string directory) : IRecordKeyProvider
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<byte[]> GetKeyAsync(string account, long epoch, CancellationToken ct)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account)))[..16].ToLowerInvariant();
        var path = Path.Combine(directory, $"d-{name}-{epoch}.key");
        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(directory);
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using var file = new FileStream(path, options);
                await file.WriteAsync(Encoding.ASCII.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))), ct);
            }

            var key = Convert.FromBase64String((await File.ReadAllTextAsync(path, ct)).Trim());
            return key.Length == 32 ? key : throw new CryptographicException($"The key file for epoch {epoch} does not hold a 256-bit key.");
        }
        finally { _lock.Release(); }
    }
}
