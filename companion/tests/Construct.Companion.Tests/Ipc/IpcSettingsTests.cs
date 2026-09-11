using System.Text;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Ipc;
namespace Construct.Companion.Tests.Ipc;

public sealed class IpcSettingsTests
{
    [Fact]
    public void MergeRoundTripKeepsNestedObjectsAndRejectsUnknownKeys()
    {
        var files = new FakeFileSystem(); files.Roots[Core.Abstractions.FileSystemRoot.LocalAppData] = "/state";
        var settings = new IpcSettings(files, new IpcEvents()); var changes = new List<string>(); settings.Changed += s => changes.Add(s.UiTheme);
        Assert.True(settings.Read().Notifications);
        settings.Merge(new JsonObject { ["forwards"] = new JsonObject { ["hostLabel"] = "laptop" }, ["uiTheme"] = "terminal" });
        settings.SaveBounds("panel", new(1, 2, 800, 600)); settings.SaveBounds("settings", new(3, 4, 600, 700));
        Assert.True(settings.Read().Forwards.Enabled); Assert.Equal("laptop", settings.Read().Forwards.HostLabel);
        Assert.Equal(new WindowBounds(1, 2, 800, 600), settings.Bounds("panel")); Assert.Equal(new WindowBounds(3, 4, 600, 700), settings.Bounds("settings"));
        Assert.Equal(3, changes.Count);
        Assert.Equal(400, Assert.Throws<IpcFailure>(() => settings.Merge(new JsonObject { ["unknown"] = true })).Status);
        Assert.Equal(400, Assert.Throws<IpcFailure>(() => settings.Merge(new JsonObject { ["uiTheme"] = "neon" })).Status);
        Assert.Equal(400, Assert.Throws<IpcFailure>(() => settings.Merge(new JsonObject { ["v"] = 2 })).Status);
    }
    [Fact]
    public void CorruptFileReadsAsDefaultsAndIsReplacedByTheNextMerge()
    {
        var files = new FakeFileSystem(); files.Roots[Core.Abstractions.FileSystemRoot.LocalAppData] = "/state";
        var settings = new IpcSettings(files, new IpcEvents());
        files.WriteFileAtomic(settings.PathName, "bad"u8);
        Assert.True(settings.Read().Notifications); Assert.Null(settings.Bounds("panel"));
        Assert.Equal("bad", Encoding.UTF8.GetString(files.ReadFile(settings.PathName)!));
        Assert.False(settings.Merge(new JsonObject { ["notifications"] = false }).Notifications);
        Assert.False(settings.Read().Notifications);
    }
    [Theory]
    [InlineData("{\"v\":2,\"notifications\":false}", "unsupportedSettings")]
    [InlineData("{\"v\":1,\"uiTheme\":\"neon\"}", "invalidSettings")]
    [InlineData("{\"v\":1,\"micDevice\":null}", "invalidSettings")]
    [InlineData("{\"v\":1,\"repatchDelaySeconds\":-5}", "invalidSettings")]
    public void UnsupportedOrInvalidStoredFileIsRefusedAndNeverRewritten(string content, string code)
    {
        var files = new FakeFileSystem(); files.Roots[Core.Abstractions.FileSystemRoot.LocalAppData] = "/state";
        var settings = new IpcSettings(files, new IpcEvents()); files.WriteFileAtomic(settings.PathName, Encoding.UTF8.GetBytes(content));
        Assert.Equal(code, Assert.Throws<IpcFailure>(() => settings.Read()).Code);
        Assert.Equal(code, Assert.Throws<IpcFailure>(() => settings.Merge(new JsonObject { ["notifications"] = false })).Code);
        Assert.Equal(content, Encoding.UTF8.GetString(files.ReadFile(settings.PathName)!));
        Assert.Equal(code, Assert.Throws<IpcFailure>(() => settings.Bounds("panel")).Code);
    }
    [Fact]
    public void StateDirectorySkipsAnEmptyLocalAppDataLikeHostJs()
    {
        var files = new FakeFileSystem(); files.Roots[Core.Abstractions.FileSystemRoot.LocalAppData] = ""; files.Roots[Core.Abstractions.FileSystemRoot.Temp] = "/temp";
        Assert.Equal(Path.Combine("/temp", "The-Construct", "companion"), new IpcSettings(files, new IpcEvents()).Directory);
        files.Roots.Remove(Core.Abstractions.FileSystemRoot.LocalAppData);
        Assert.Equal(Path.Combine("/temp", "The-Construct", "companion"), new IpcSettings(files, new IpcEvents()).Directory);
        files.Roots[Core.Abstractions.FileSystemRoot.LocalAppData] = "/local";
        Assert.Equal(Path.Combine("/local", "The-Construct", "companion"), new IpcSettings(files, new IpcEvents()).Directory);
    }
    [Fact]
    public void ClientPatchesClampTheRepatchDelayButStoredValuesAreNotRewritten()
    {
        var files = new FakeFileSystem(); files.Roots[Core.Abstractions.FileSystemRoot.LocalAppData] = "/state";
        var settings = new IpcSettings(files, new IpcEvents());
        Assert.Equal(0, settings.Merge(new JsonObject { ["repatchDelaySeconds"] = -1 }).RepatchDelaySeconds);
        Assert.Equal(600, settings.Merge(new JsonObject { ["repatchDelaySeconds"] = 1000 }).RepatchDelaySeconds);
        Assert.Equal(45, settings.Merge(new JsonObject { ["repatchDelaySeconds"] = 45 }).RepatchDelaySeconds);
    }
    [Fact]
    public void BoundsSaveIsBestEffortWhenTheFileIsNotWritable()
    {
        var files = new FakeFileSystem { WriteFailure = new UnauthorizedAccessException("denied") }; files.Roots[Core.Abstractions.FileSystemRoot.LocalAppData] = "/state";
        var settings = new IpcSettings(files, new IpcEvents());
        Assert.IsType<UnauthorizedAccessException>(settings.TrySaveBounds("panel", new(1, 2, 3, 4)));
        files.WriteFailure = null; Assert.Null(settings.TrySaveBounds("panel", new(1, 2, 3, 4))); Assert.Equal(new WindowBounds(1, 2, 3, 4), settings.Bounds("panel"));
        files.WriteFileAtomic(settings.PathName, "{\"v\":2}"u8); Assert.IsType<IpcFailure>(settings.TrySaveBounds("panel", new(1, 2, 3, 4)));
    }
}
