using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Fakes;
public sealed class FakeConfigSyncStorage(IFileSystem files, IClock clock) : IConfigSyncStorage
{
    private readonly object gate = new();
    public Dictionary<string, DateTimeOffset> Modified { get; } = [];
    public HashSet<int> DeadProcesses { get; } = [];
    public int ProcessId { get; set; } = 42;
    public bool TryCreateFile(string path, string content)
    {
        lock (gate) { if (files.FileExists(path)) return false; files.WriteFileAtomic(path, System.Text.Encoding.UTF8.GetBytes(content)); Modified[path] = clock.UtcNow; return true; }
    }
    public DateTimeOffset? LastWriteTime(string path) => files.FileExists(path) ? Modified.GetValueOrDefault(path, clock.UtcNow) : null;
    public bool DirectoryExists(string path) => files.EnumerateDirectories(Path.GetDirectoryName(path) ?? "").Contains(path);
    public void DeleteDirectory(string path) { foreach (var p in files.EnumerateFiles(path)) files.DeleteFile(p); foreach (var p in files.EnumerateDirectories(path)) DeleteDirectory(p); }
    public bool ProcessIsDefinitelyDead(int pid) => DeadProcesses.Contains(pid);
}
