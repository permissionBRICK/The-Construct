using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Fakes;

public sealed class FakeFileSystem : IStateFileSystem
{
    private readonly object gate = new();
    public Dictionary<FileSystemRoot, string> Roots { get; } = [];
    public Dictionary<string, DateTimeOffset> Modified { get; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Directory, Action Changed)> watches = [];
    private static string Key(string path) => path.Replace('\\', '/').TrimEnd('/');
    private static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    public string? GetRoot(FileSystemRoot root) => Roots.GetValueOrDefault(root);
    public bool FileExists(string path) { lock (gate) return files.ContainsKey(Key(path)); }
    public byte[]? ReadFile(string path) { lock (gate) return files.TryGetValue(Key(path), out var value) ? value.ToArray() : null; }
    public void WriteFileAtomic(string path, ReadOnlySpan<byte> contents)
    {
        var key = Key(path); lock (gate) { CreateDirectory(Parent(key)); files[key] = contents.ToArray(); } Changed(key);
    }
    public void DeleteFile(string path) { bool removed; lock (gate) removed = files.Remove(Key(path)); if (removed) Changed(Key(path)); }
    public void CreateDirectory(string path)
    {
        var key = Key(path);
        lock (gate) while (key.Length > 0 && directories.Add(key)) key = Parent(key);
    }
    public IReadOnlyList<string> EnumerateFiles(string directory) { lock (gate) return files.Keys.Where(p => string.Equals(Parent(p), Key(directory), StringComparison.OrdinalIgnoreCase)).Order().ToArray(); }
    public IReadOnlyList<string> EnumerateDirectories(string directory) { lock (gate) return directories.Where(p => string.Equals(Parent(p), Key(directory), StringComparison.OrdinalIgnoreCase)).Order().ToArray(); }
    public bool DirectoryExists(string path) { lock (gate) return directories.Contains(Key(path)); }
    public DateTimeOffset? LastWriteTime(string path) => FileExists(path) ? Modified.GetValueOrDefault(path, DateTimeOffset.UnixEpoch) : null;
    public bool WriteFileIfAbsent(string path, ReadOnlySpan<byte> contents)
    {
        lock (gate) { if (FileExists(path)) return false; WriteFileAtomic(path, contents); return true; }
    }
    public IDisposable Watch(string directory, Action changed)
    {
        var watch = (Key(directory), changed); lock (gate) watches.Add(watch); return new Subscription(() => { lock (gate) watches.Remove(watch); });
    }
    private void Changed(string path)
    {
        (string Directory, Action Changed)[] snapshot; lock (gate) snapshot = watches.ToArray();
        foreach (var watch in snapshot)
            if (path.StartsWith(watch.Directory + "/", StringComparison.OrdinalIgnoreCase)) watch.Changed();
    }
    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
