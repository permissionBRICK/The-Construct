using System.Text;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.State;
using Construct.Companion.Host.Runtime;

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
    // The real OS file system with a throwaway %LOCALAPPDATA%/%TEMP% root.
    private sealed class TemporaryFiles : IStateFileSystem, IDisposable
    {
        private readonly HostFileSystem inner = new();
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "companion-state-" + Guid.NewGuid().ToString("N"));
        public TemporaryFiles() => Directory.CreateDirectory(Root);
        public string? GetRoot(FileSystemRoot root) => root is FileSystemRoot.LocalAppData or FileSystemRoot.Temp ? Root : null;
        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public DateTimeOffset? LastWriteTime(string path) => inner.LastWriteTime(path);
        public byte[]? ReadFile(string path) => inner.ReadFile(path);
        public void WriteFileAtomic(string path, ReadOnlySpan<byte> contents) => inner.WriteFileAtomic(path, contents);
        public bool WriteFileIfAbsent(string path, ReadOnlySpan<byte> contents) => inner.WriteFileIfAbsent(path, contents);
        public void DeleteFile(string path) => inner.DeleteFile(path);
        public void DeleteDirectory(string path) => inner.DeleteDirectory(path);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public IReadOnlyList<string> EnumerateFiles(string directory) => inner.EnumerateFiles(directory);
        public IReadOnlyList<string> EnumerateDirectories(string directory) => inner.EnumerateDirectories(directory);
        public IDisposable Watch(string directory, Action changed) => throw new NotSupportedException();
        public void Dispose() => Directory.Delete(Root, true);
    }
}
