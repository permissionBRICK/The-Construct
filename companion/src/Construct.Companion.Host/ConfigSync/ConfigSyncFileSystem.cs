using System.Diagnostics;
using System.Text;
using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Host.ConfigSync;

// Portable adapter; Windows-specific registrations remain in the composition root.
public sealed class ConfigSyncFileSystem : IFileSystem, IConfigSyncStorage
{
    public string? GetRoot(FileSystemRoot root) => root switch { FileSystemRoot.Temp => Path.GetTempPath(), FileSystemRoot.UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), FileSystemRoot.LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FileSystemRoot.InstallDirectory => AppContext.BaseDirectory, _ => null };
    public bool FileExists(string path) => File.Exists(path);
    public byte[]? ReadFile(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    public void WriteFileAtomic(string path, ReadOnlySpan<byte> contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, contents); File.Move(temp, path, true); } finally { File.Delete(temp); }
    }
    public void DeleteFile(string path) => File.Delete(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public IReadOnlyList<string> EnumerateFiles(string directory) => Directory.Exists(directory) ? Directory.GetFiles(directory).Order(StringComparer.Ordinal).ToArray() : [];
    public IReadOnlyList<string> EnumerateDirectories(string directory) => Directory.Exists(directory) ? Directory.GetDirectories(directory).Order(StringComparer.Ordinal).ToArray() : [];
    public IDisposable Watch(string directory, Action changed)
    {
        var watcher = new FileSystemWatcher(directory) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        watcher.Changed += (_, _) => changed(); watcher.Created += (_, _) => changed(); watcher.Deleted += (_, _) => changed(); watcher.Renamed += (_, _) => changed(); watcher.Error += (_, _) => changed(); watcher.EnableRaisingEvents = true; return watcher;
    }
    public bool TryCreateFile(string path, string content)
    {
        try { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); stream.Write(Encoding.UTF8.GetBytes(content)); return true; }
        catch (IOException) when (File.Exists(path)) { return false; }
    }
    public DateTimeOffset? LastWriteTime(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void DeleteDirectory(string path) { if (Directory.Exists(path)) Directory.Delete(path, true); }
    public int ProcessId => Environment.ProcessId;
    public bool ProcessIsDefinitelyDead(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
