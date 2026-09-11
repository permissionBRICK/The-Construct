using Construct.Companion.Core.Abstractions;
using Construct.Companion.Host.ConfigSync;
namespace Construct.Companion.Host.Desktop;

public sealed class DesktopFileSystem : IStateFileSystem
{
    private readonly ConfigSyncFileSystem inner = new();
    public string? GetRoot(FileSystemRoot root) => inner.GetRoot(root);
    public bool FileExists(string path) => inner.FileExists(path);
    public byte[]? ReadFile(string path) => inner.ReadFile(path);
    public void WriteFileAtomic(string path, ReadOnlySpan<byte> contents) => inner.WriteFileAtomic(path, contents);
    public void DeleteFile(string path) => inner.DeleteFile(path);
    public void CreateDirectory(string path) => inner.CreateDirectory(path);
    public IReadOnlyList<string> EnumerateFiles(string directory) => inner.EnumerateFiles(directory);
    public IReadOnlyList<string> EnumerateDirectories(string directory) => inner.EnumerateDirectories(directory);
    public IDisposable Watch(string directory, Action changed) => inner.Watch(directory, changed);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public DateTimeOffset? LastWriteTime(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    public bool WriteFileIfAbsent(string path, ReadOnlySpan<byte> contents)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllBytes(temporary, contents);
            try { File.Move(temporary, path, false); return true; }
            catch (IOException) when (File.Exists(path)) { return false; }
        }
        finally { File.Delete(temporary); }
    }
}
