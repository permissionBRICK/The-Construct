using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Fakes;

public sealed class FakeStateFileSystem : IStateFileSystem
{
    public FakeFileSystem Files { get; } = new();
    public Dictionary<string, DateTimeOffset> Modified { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? GetRoot(FileSystemRoot root) => Files.GetRoot(root);
    public bool FileExists(string path) => Files.FileExists(path);
    public byte[]? ReadFile(string path) => Files.ReadFile(path);
    public void WriteFileAtomic(string path, ReadOnlySpan<byte> contents) => Files.WriteFileAtomic(path, contents);
    public void DeleteFile(string path) => Files.DeleteFile(path);
    public void CreateDirectory(string path) => Files.CreateDirectory(path);
    public IReadOnlyList<string> EnumerateFiles(string directory) => Files.EnumerateFiles(directory);
    public IReadOnlyList<string> EnumerateDirectories(string directory) => Files.EnumerateDirectories(directory);
    public IDisposable Watch(string directory, Action changed) => Files.Watch(directory, changed);
    public bool DirectoryExists(string path) => Files.EnumerateDirectories(Path.GetDirectoryName(path) ?? "").Any(p => p.Equals(path.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
    public DateTimeOffset? LastWriteTime(string path) => FileExists(path) ? Modified.GetValueOrDefault(path, DateTimeOffset.UnixEpoch) : null;
    public bool WriteFileIfAbsent(string path, ReadOnlySpan<byte> contents)
    {
        lock (Files) { if (FileExists(path)) return false; WriteFileAtomic(path, contents); return true; }
    }
}
