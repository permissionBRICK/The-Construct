using System.Security.Cryptography;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Fakes;

/// <summary>Files are confined to a disposable test directory. No network requests are made.</summary>
public sealed class FakeMediaTransfer : IMediaTransfer, IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "construct-media-fake-" + Guid.NewGuid().ToString("n"));
    public Dictionary<Uri, byte[]> Sources { get; } = [];
    public Dictionary<string, string> UploadPaths { get; } = [];
    public bool FailWrites { get; set; }
    public bool FilesHeldOpen { get; set; }
    public FakeMediaTransfer() => Directory.CreateDirectory(Root);
    private string Confine(string path)
    {
        var full = Path.GetFullPath(path, Root);
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new IOException("Fake media path is outside its root.");
        return full;
    }
    public async Task<TransferResult> AcquireAsync(MediaItem item, Uri source, long maxBytes, TimeSpan timeout, IProgress<string>? progress, CancellationToken ct)
    {
        if (FailWrites || timeout <= TimeSpan.Zero || !Sources.TryGetValue(source, out var bytes)) throw new IOException("Fake transfer failed.");
        if (bytes.LongLength > maxBytes) throw new IOException("Media exceeds the byte limit.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (item.ExpectedSha256 is not null && !StringComparer.OrdinalIgnoreCase.Equals(hash, item.ExpectedSha256)) throw new IOException("Checksum mismatch.");
        await File.WriteAllBytesAsync(Confine(item.Path), bytes, ct);
        return new(bytes.LongLength, hash, source);
    }
    public async Task WriteChunkAsync(MediaUpload upload, int index, Stream body, long contentLength, CancellationToken ct)
    {
        var offset = checked((long)index * upload.ChunkBytes);
        if (FailWrites || index < 0 || offset >= upload.SizeBytes || contentLength != Math.Min(upload.ChunkBytes, upload.SizeBytes - offset)) throw new IOException("Invalid fake upload chunk.");
        var bytes = new byte[checked((int)contentLength)];
        await body.ReadExactlyAsync(bytes, ct);
        if (await body.ReadAsync(new byte[1], ct) != 0) throw new IOException("Chunk exceeds declared length.");
        var path = Confine(UploadPaths.GetValueOrDefault(upload.Id) ?? upload.MediaId + ".part");
        await using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        file.Position = offset; await file.WriteAsync(bytes, ct);
    }
    public async Task<string> HashAsync(string path, IProgress<string>? progress, CancellationToken ct)
    { await using var file = File.OpenRead(Confine(path)); return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)); }
    public async Task<bool> LooksLikeIsoAsync(string path, CancellationToken ct)
    { var bytes = await File.ReadAllBytesAsync(Confine(path), ct); return bytes.Length >= 32774 && bytes.AsSpan(32769, 5).SequenceEqual("CD001"u8); }
    public Task<bool> TryDeleteAsync(string path, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); var full = Confine(path); if (FilesHeldOpen) return Task.FromResult(false); File.Delete(full); return Task.FromResult(true); }
    public Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Directory.GetFiles(Root));
    public void Dispose() => Directory.Delete(Root, true);
}
