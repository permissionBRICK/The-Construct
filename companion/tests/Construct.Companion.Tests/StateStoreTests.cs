using System.Text;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.State;
using Construct.Companion.Fakes;

namespace Construct.Companion.Tests;

public sealed class StateStoreTests
{
    private static FakeFileSystem Files()
    {
        var fs = new FakeFileSystem(); fs.Roots[FileSystemRoot.LocalAppData] = "/local"; fs.CreateDirectory("/scripts"); return fs;
    }
    [Fact]
    public void DefaultOnlyInstallDoesNotCreateInstanceFile()
    {
        var fs = Files(); var store = new InstanceStateStore(fs, "agent-vm", "/scripts");
        fs.WriteFileAtomic("/scripts/.construct-settings.json", Encoding.UTF8.GetBytes("\ufeff{\"installedCommit\":\"old\",\"unmanaged\":\"ü\"}"));
        store.SaveSettings(new JsonObject { ["ram"] = "8", ["mic"] = false });
        Assert.Null(store.StatePath);
        Assert.Empty(fs.EnumerateFiles("/local/The-Construct/instances"));
        Assert.Equal("{\n  \"installedCommit\": \"old\",\n  \"unmanaged\": \"ü\",\n  \"vmMemoryGB\": 8,\n  \"micPassthrough\": false\n}\n", Encoding.UTF8.GetString(fs.ReadFile("/scripts/.construct-settings.json")!));
    }
    [Fact]
    public void NamedStoreSplitsAndDoesNotInheritDefaultVmSettings()
    {
        var fs = Files(); var host = new HostState(fs); host.WriteRawSettings("/scripts", new JsonObject { ["installedCommit"] = "abc", ["vmMemoryGB"] = 8, ["gitUserName"] = "owner" });
        var store = new InstanceStateStore(fs, "dev", "/scripts");
        store.SaveState(new JsonObject { ["gitEmail"] = "user@example", ["vmMemoryGB"] = 16, ["projects"] = new JsonArray("api") });
        Assert.Equal(8, host.ReadRawSettings("/scripts")["vmMemoryGB"]!.GetValue<int>());
        Assert.Equal("abc", StateJson.Text(store.ReadMerged()["installedCommit"]));
        Assert.Null(store.ReadState()["gitEmail"]);
        Assert.Equal("{\n  \"version\": 1,\n  \"instance\": \"dev\",\n  \"projects\": [\n    \"api\"\n  ],\n  \"vmMemoryGB\": 16\n}\n", Encoding.UTF8.GetString(fs.ReadFile(store.StatePath!)!));
    }
    [Fact]
    public void InstallOnlyPatchDoesNotCreateEmptyStateAndHandEditedWideKeysCannotShadow()
    {
        var fs = Files(); var store = new InstanceStateStore(fs, "dev", "/scripts");
        store.SaveState(new JsonObject { ["installedCommit"] = "right" }); Assert.False(fs.FileExists(store.StatePath!));
        fs.WriteFileAtomic(store.StatePath!, Encoding.UTF8.GetBytes("\ufeff{\"version\":1,\"instance\":\"dev\",\"installedCommit\":\"wrong\",\"vmCpuCount\":4}"));
        Assert.Equal("right", StateJson.Text(store.ReadMerged()["installedCommit"])); Assert.Single(store.ReadState());
    }
    [Theory]
    [InlineData("../escape")]
    [InlineData("Agent-VM")]
    [InlineData("construct-dev")]
    public void InvalidNamesCannotWrite(string name)
    {
        var fs = Files(); var store = new InstanceStateStore(fs, name, "/scripts");
        Assert.Null(store.StatePath); Assert.Throws<InvalidOperationException>(() => store.SaveState(new JsonObject { ["micPassthrough"] = true }));
    }
    [Fact]
    public void SelectionAndAppliedMarkerAreIsolatedAndClearable()
    {
        var fs = Files(); var a = new InstanceStateStore(fs, "a", "/scripts"); var b = new InstanceStateStore(fs, "b", "/scripts");
        Assert.False(a.HasPersistedSelection()); a.SaveSelectedProjects(new JsonArray(" api ", "api", "../x", "bad:name"));
        Assert.Single(a.ReadSelectedProjects()); Assert.False(b.HasPersistedSelection());
        a.SaveSelectedProjects(new JsonArray()); Assert.True(a.HasPersistedSelection());
        a.SaveAppliedAutoCheckpoints(false); Assert.False(a.ReadAppliedAutoCheckpoints()); a.SaveAppliedAutoCheckpoints(null);
        Assert.False(a.ReadState().ContainsKey("vmAutoCheckpointsApplied"));
    }
    [Fact]
    public void DiscoveryUsesNewestMarkerAndOverridePrecedence()
    {
        var fs = Files(); fs.WriteFileAtomic("/local/The-Construct/one/repo/Auto-Install.ps1", []); fs.WriteFileAtomic("/local/The-Construct/two/Auto-Install.ps1", []);
        fs.Modified["/local/The-Construct/two/Auto-Install.ps1"] = DateTimeOffset.UnixEpoch.AddDays(1);
        var host = new HostState(fs); Assert.Equal("/local/The-Construct/two", host.ResolveScriptsDirectory());
        Assert.Equal("/scripts", host.ResolveScriptsDirectory("/scripts", "/local/The-Construct/two"));
        Assert.Equal("/local/The-Construct/two", host.ResolveScriptsDirectory("/stale"));
        fs.Roots.Remove(FileSystemRoot.LocalAppData); fs.Roots[FileSystemRoot.Temp] = "/temp"; Assert.Equal("/temp", host.LocalAppData);
    }
    [Fact]
    public void ProfilesAreTraversalSafeAndCreateDoesNotOverwriteCaseVariants()
    {
        var fs = Files(); var host = new HostState(fs); var first = new JsonObject { ["name"] = "api", ["unknown"] = true };
        Assert.True(host.WriteProjectProfileIfAbsent("/scripts", "api", first));
        Assert.False(host.WriteProjectProfileIfAbsent("/scripts", "API", new JsonObject()));
        Assert.True(host.ReadProjectProfile("/scripts", "api")!["unknown"]!.GetValue<bool>());
        Assert.Throws<ArgumentException>(() => host.WriteProjectProfile("/scripts", "../x", first));
        Assert.Equal(["api"], host.ListProjectProfiles("/scripts")); Assert.True(host.DeleteProjectProfile("/scripts", "api")); Assert.False(host.DeleteProjectProfile("/scripts", "api"));
    }
    [Fact]
    public void BackfillOnlyRecordsAbsentTrustworthyValues()
    {
        var fs = Files(); var store = new InstanceStateStore(fs, "dev", "/scripts"); store.SaveState(new JsonObject { ["vmMemoryGB"] = 16 });
        var probe = new JsonObject { ["online"] = true, ["vmSpec"] = new JsonObject { ["ramGb"] = 8, ["cpus"] = 4 }, ["vmConfig"] = new JsonObject { ["t3code"] = true } };
        Assert.Equal(2, store.BackfillFromProbe(probe)!.Count); Assert.Equal(16, store.ReadState()["vmMemoryGB"]!.GetValue<int>());
        Assert.Null(store.BackfillFromProbe(probe));
        probe["probeError"] = true; var other = new InstanceStateStore(fs, "other", "/scripts"); Assert.Null(other.BackfillFromProbe(probe)); Assert.False(fs.FileExists(other.StatePath!));
        probe["online"] = false; Assert.Null(store.BackfillFromProbe(probe));
    }
}
