using System.Text;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.State;

namespace Construct.Companion.Tests;

public sealed class TemporaryStoreTests
{
    [Fact]
    public void StoresInteroperateInTemporaryLocalAppDataTree()
    {
        using var files = new TemporaryFiles(); var scripts = Path.Combine(files.Root, "The-Construct", "repo-main", "repo"); files.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "Auto-Install.ps1"), "# marker");
        File.WriteAllText(Path.Combine(scripts, ".construct-settings.json"), "{\"installedCommit\":\"abcdef123\",\"gitUserName\":\"ü\",\"vmMemoryGB\":8}", new UTF8Encoding(true));
        var host = new HostState(files); Assert.Equal(scripts, host.ResolveScriptsDirectory());
        var legacy = new InstanceStateStore(files, "agent-vm", scripts); var named = new InstanceStateStore(files, "dev", scripts);
        Assert.Equal("ü", StateJson.Text(named.ReadMerged()["gitUserName"])); Assert.Null(named.ReadMerged()["vmMemoryGB"]);
        named.SaveState(new JsonObject { ["vmMemoryGB"] = 16, ["gitEmail"] = "user@example" });
        Assert.Equal(8, legacy.ReadState()["vmMemoryGB"]!.GetValue<int>()); Assert.Equal(16, named.ReadState()["vmMemoryGB"]!.GetValue<int>());
        Assert.False(File.Exists(Path.Combine(files.Root, "The-Construct", "instances", "agent-vm.json")));
        Assert.False(File.ReadAllBytes(named.StatePath!).AsSpan().StartsWith(new byte[] { 239, 187, 191 }));
        Assert.True(host.WriteProjectProfileIfAbsent(scripts, "api", new JsonObject { ["name"] = "api" })); Assert.False(host.WriteProjectProfileIfAbsent(scripts, "API", new JsonObject()));
        var registry = InstanceRegistry.Load(files).Add("dev", new JsonObject()).Remove("agent-vm"); registry.Save(files);
        var loaded = InstanceRegistry.Load(files); Assert.Single(loaded.ByName); Assert.Contains("agent-vm", loaded.Removed); Assert.Equal("dev", loaded.DefaultInstance);
        Assert.Empty(Directory.GetFiles(files.Root, "*.tmp*", SearchOption.AllDirectories));
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{broken")]
    [InlineData("\ufeff\ufeff{\"value\":1}")]
    public void MalformedSettingsReadAsEmpty(string content)
    {
        using var files = new TemporaryFiles(); File.WriteAllText(Path.Combine(files.Root, ".construct-settings.json"), content);
        Assert.Empty(new HostState(files).ReadRawSettings(files.Root));
    }
    private sealed class TemporaryFiles : IStateFileSystem, IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "companion-state-" + Guid.NewGuid().ToString("N"));
        public TemporaryFiles() => Directory.CreateDirectory(Root);
        public string? GetRoot(FileSystemRoot root) => root is FileSystemRoot.LocalAppData or FileSystemRoot.Temp ? Root : null;
        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public DateTimeOffset? LastWriteTime(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        public byte[]? ReadFile(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
        public void WriteFileAtomic(string path, ReadOnlySpan<byte> contents) => Write(path, contents, true);
        private static void Write(string path, ReadOnlySpan<byte> contents, bool overwrite)
        {
            var temp = path + ".tmp." + Guid.NewGuid().ToString("N");
            try { File.WriteAllBytes(temp, contents); File.Move(temp, path, overwrite); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public bool WriteFileIfAbsent(string path, ReadOnlySpan<byte> contents)
        {
            try { Write(path, contents, false); return true; } catch (IOException) when (File.Exists(path)) { return false; }
        }
        public void DeleteFile(string path) => File.Delete(path);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public IReadOnlyList<string> EnumerateFiles(string directory) => Directory.Exists(directory) ? Directory.GetFiles(directory).Order(StringComparer.Ordinal).ToArray() : [];
        public IReadOnlyList<string> EnumerateDirectories(string directory) => Directory.Exists(directory) ? Directory.GetDirectories(directory).Order(StringComparer.Ordinal).ToArray() : [];
        public IDisposable Watch(string directory, Action changed) => throw new NotSupportedException();
        public void Dispose() => Directory.Delete(Root, true);
    }
}
