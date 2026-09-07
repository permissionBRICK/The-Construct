using Constructd.Core.Abstractions;
namespace Constructd.Windows.Media;

/// <summary>Owns only flat, generated .part/.iso files. External sources and links are refused.</summary>
public sealed class MediaFileStore(string root, IReadOnlyList<string>? protectedPaths = null) : IMediaFiles
{
    public string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    public string PathFor(string id, bool partial = false)
    {
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) throw new MediaException("media-path-refused");
        return Path.Combine(Root, id + (partial ? ".part" : ".iso"));
    }
    private string Confine(string path)
    {
        var full = Path.GetFullPath(path);
        if(protectedPaths?.Any(p => !string.IsNullOrWhiteSpace(p) && string.Equals(Path.GetFullPath(p),full,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) == true) throw new MediaException("media-path-refused");
        var name = Path.GetFileName(full);
        if (!string.Equals(Path.GetDirectoryName(full), Root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            name.Length != 36 && name.Length != 37 || !name[..32].All(Uri.IsHexDigit) ||
            Path.GetExtension(name) is not ".iso" and not ".part") throw new MediaException("media-path-refused");
        for (var dir = new DirectoryInfo(Root); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new MediaException("media-path-refused");
        if ((File.Exists(full) || Directory.Exists(full)) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new MediaException("media-path-refused");
        return full;
    }
    public Task CreateAsync(string path, long size, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var full = Confine(path); Directory.CreateDirectory(Root);
        using var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.SetLength(size);
        return Task.CompletedTask;
    }
    public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult<Stream>(new FileStream(Confine(path), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan)); }
    public async Task WriteAsync(string path, long offset, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    { await using var file = new FileStream(Confine(path), FileMode.Open, FileAccess.Write, FileShare.Read, 65536, true); file.Position = offset; await file.WriteAsync(bytes, ct); }
    public Task PublishAsync(string partial, string destination, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); File.Move(Confine(partial), Confine(destination), false); return Task.CompletedTask; }
    public Task<bool> DeleteAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var full = Confine(path);
        try { File.Delete(full); return Task.FromResult(!File.Exists(full)); }
        catch (IOException) { return Task.FromResult(false); }
    }
    public Task<IReadOnlyList<(string Path, DateTimeOffset Modified)>> ListAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); if (!Directory.Exists(Root)) return Task.FromResult<IReadOnlyList<(string, DateTimeOffset)>>([]);
        var result = new List<(string, DateTimeOffset)>();
        foreach (var path in Directory.EnumerateFiles(Root))
        { try { result.Add((Confine(path), File.GetLastWriteTimeUtc(path))); } catch (MediaException) { } }
        return Task.FromResult<IReadOnlyList<(string, DateTimeOffset)>>(result);
    }
}
