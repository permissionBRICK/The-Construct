using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Fakes;

public sealed class FakeFileSystem : IFileSystem
{
    public Dictionary<FileSystemRoot, string> Roots { get; } = [];
    private readonly Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Directory, Action Changed)> watches = [];
    private static string Key(string path) => path.Replace('\\', '/').TrimEnd('/');
    private static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    public string? GetRoot(FileSystemRoot root) => Roots.GetValueOrDefault(root);
    public bool FileExists(string path) => files.ContainsKey(Key(path));
    public byte[]? ReadFile(string path) => files.TryGetValue(Key(path), out var value) ? value.ToArray() : null;
    public void WriteFileAtomic(string path, ReadOnlySpan<byte> contents)
    {
        var key = Key(path); CreateDirectory(Parent(key)); files[key] = contents.ToArray(); Changed(key);
    }
    public void DeleteFile(string path) { if (files.Remove(Key(path))) Changed(Key(path)); }
    public void CreateDirectory(string path)
    {
        var key = Key(path);
        while (key.Length > 0 && directories.Add(key)) key = Parent(key);
    }
    public IReadOnlyList<string> EnumerateFiles(string directory) => files.Keys.Where(p => string.Equals(Parent(p), Key(directory), StringComparison.OrdinalIgnoreCase)).Order().ToArray();
    public IReadOnlyList<string> EnumerateDirectories(string directory) => directories.Where(p => string.Equals(Parent(p), Key(directory), StringComparison.OrdinalIgnoreCase)).Order().ToArray();
    public IDisposable Watch(string directory, Action changed)
    {
        var watch = (Key(directory), changed); watches.Add(watch); return new Subscription(() => watches.Remove(watch));
    }
    private void Changed(string path)
    {
        foreach (var watch in watches.ToArray())
            if (path.StartsWith(watch.Directory + "/", StringComparison.OrdinalIgnoreCase)) watch.Changed();
    }
    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
