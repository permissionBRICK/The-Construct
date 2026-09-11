using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Host.Runtime;

// %LOCALAPPDATA% and %TEMP% resolve exactly as host.js localAppData does; the fake replaces this on Linux.
public sealed class HostFileSystem : IStateFileSystem
{
    public string? GetRoot(FileSystemRoot root) => root switch
    {
        FileSystemRoot.LocalAppData => Environment.GetEnvironmentVariable("LOCALAPPDATA"),
        FileSystemRoot.Temp => Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(),
        FileSystemRoot.UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        FileSystemRoot.InstallDirectory => AppContext.BaseDirectory,
        _ => null
    };
    public bool FileExists(string path) => File.Exists(path);
    public byte[]? ReadFile(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    public void WriteFileAtomic(string path, ReadOnlySpan<byte> contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, contents); File.Move(temporary, path, true); }
        finally { File.Delete(temporary); }
    }
    public bool WriteFileIfAbsent(string path, ReadOnlySpan<byte> contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, contents);
            try { File.Move(temporary, path, false); return true; }
            catch (IOException) when (File.Exists(path)) { return false; }
        }
        finally { File.Delete(temporary); }
    }
    public void DeleteFile(string path) => File.Delete(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void DeleteDirectory(string path) { if (Directory.Exists(path)) Directory.Delete(path, true); }
    public DateTimeOffset? LastWriteTime(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    public IReadOnlyList<string> EnumerateFiles(string directory) => Directory.Exists(directory) ? Directory.GetFiles(directory).Order(StringComparer.Ordinal).ToArray() : [];
    public IReadOnlyList<string> EnumerateDirectories(string directory) => Directory.Exists(directory) ? Directory.GetDirectories(directory).Order(StringComparer.Ordinal).ToArray() : [];
    // Recursive: the state root watch must see instances/*.json and companion/settings.json replacements.
    public IDisposable Watch(string directory, Action changed)
    {
        Directory.CreateDirectory(directory);
        var watcher = new FileSystemWatcher(directory) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        watcher.Changed += (_, _) => changed(); watcher.Created += (_, _) => changed();
        watcher.Deleted += (_, _) => changed(); watcher.Renamed += (_, _) => changed(); watcher.Error += (_, _) => changed();
        watcher.EnableRaisingEvents = true;
        return watcher;
    }
}
