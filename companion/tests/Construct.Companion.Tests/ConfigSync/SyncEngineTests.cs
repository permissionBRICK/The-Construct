using Construct.Companion.Core.ConfigSync;
using Construct.Companion.Host.ConfigSync;
namespace Construct.Companion.Tests.ConfigSync;
public sealed class SyncEngineTests
{
    [Fact] public async Task SeedAndVmEditRoundTripWithRealGit()
    {
        using var w = new GitTestWorkspace(); w.Host("a");
        var seed = await w.Engine.SyncTickAsync(); Assert.True(seed.Ok); Assert.True(seed.Seeded); Assert.Equal(["a"],seed.WriteBack.Done);
        w.Vm("a",GitTestWorkspace.Profile("a","echo vm")); var tick = await w.Engine.SyncTickAsync(); Assert.True(tick.Ok); Assert.True(tick.Merged);
        Assert.Equal(File.ReadAllText(Path.Combine(w.Store,"a.json")),w.Repo.ReadMainProfiles()["a"]); Assert.Equal(await w.G("rev-parse","main"),await w.G("rev-parse","vm"));
        Assert.Empty(await w.G("status","--porcelain"));
    }
    [Fact] public async Task ConflictStaysInGitUntilResolvedThenRecovers()
    {
        using var w = new GitTestWorkspace(); w.Host("a"); await w.Engine.SyncTickAsync();
        w.Host("a",GitTestWorkspace.Profile("a","host")); w.Vm("a",GitTestWorkspace.Profile("a","vm"));
        var result = await w.Engine.SyncTickAsync(); Assert.False(result.Ok); Assert.True(result.Conflict); Assert.Contains("projects/a.json",(await w.Repo.StateAsync()).ConflictFiles);
        w.Host("a",GitTestWorkspace.Profile("a","resolved")); await w.G("add","projects/a.json");
        result = await w.Engine.SyncTickAsync(); Assert.True(result.Ok); Assert.False((await w.Repo.StateAsync()).MergeInProgress); Assert.Contains("resolved",File.ReadAllText(Path.Combine(w.Store,"a.json")));
    }
    [Fact] public async Task InvalidHostEditBlocksMergeWithoutOverwriting()
    {
        using var w = new GitTestWorkspace(); w.Host("a"); await w.Engine.SyncTickAsync();
        w.Host("a","{ invalid"); w.Vm("a",GitTestWorkspace.Profile("a","vm"));
        var result = await w.Engine.SyncTickAsync(); Assert.True(result.Blocked); Assert.False(result.Ok); Assert.Equal("{ invalid",w.Repo.ReadMainProfiles()["a"]);
    }
    [Fact] public async Task InvalidVmFileIsPreservedInsteadOfDeleted()
    {
        using var w = new GitTestWorkspace(); w.Host("a"); w.Host("b"); await w.Engine.SyncTickAsync(); var old = await w.G("show","vm:projects/a.json");
        w.Vm("a","half-written"); w.Vm("b",GitTestWorkspace.Profile("b","changed")); var result = await w.Engine.SyncTickAsync();
        Assert.True(result.Ok); Assert.Single(result.SkippedInvalid); Assert.Equal(old,(await w.G("show","vm:projects/a.json"))); Assert.Equal("half-written",File.ReadAllText(Path.Combine(w.Store,"a.json"))); Assert.Contains("a",result.WriteBack.Skipped);
    }
    [Fact] public async Task EmptyExistingStoreReseedsWithoutCommittingMassDeletion()
    {
        using var w = new GitTestWorkspace(); w.Host("a"); await w.Engine.SyncTickAsync(); var tip = await w.G("rev-parse","vm"); File.Delete(Path.Combine(w.Store,"a.json"));
        var result = await w.Engine.SyncTickAsync(); Assert.True(result.Ok); Assert.True(result.Seeded); Assert.Contains(result.Warnings,s=>s.Contains("mass deletion",StringComparison.Ordinal)); Assert.Equal(tip,await w.G("rev-parse","vm")); Assert.True(File.Exists(Path.Combine(w.Store,"a.json")));
    }
    [Fact] public async Task MissingFileIsRestoredWithoutChangingMain()
    {
        using var w = new GitTestWorkspace(); w.Host("a"); w.Host("b"); await w.Engine.SyncTickAsync(); File.Delete(Path.Combine(w.Store,"a.json"));
        var main = await w.G("rev-parse","main");
        Assert.True((await w.Engine.SyncTickAsync()).Ok); Assert.True(w.Repo.ReadMainProfiles().ContainsKey("a")); Assert.True(File.Exists(Path.Combine(w.Store,"a.json"))); Assert.Equal(main,await w.G("rev-parse","main"));
    }
    private static string Delete(GitTestWorkspace w, string name)
    {
        Directory.CreateDirectory(Path.Combine(w.Store,".deleted")); var marker = Path.Combine(w.Store,".deleted",name);
        File.WriteAllText(marker,""); File.Delete(Path.Combine(w.Store,name+".json")); return marker;
    }
    [Fact] public async Task ExplicitDeletionPropagatesToAnotherInstanceAndClearsMarker()
    {
        using var w = new GitTestWorkspace(); w.Host("a"); w.Host("b"); await w.Engine.SyncTickAsync();
        var otherStore = Path.Combine(w.Root,"other-store"); var other = new ConfigSyncEngine(w.Repo,w.Lock,w.Ssh,"vm-other",otherStore); await other.SyncTickAsync();
        var marker = Delete(w,"a"); var result = await w.Engine.SyncTickAsync();
        Assert.True(result.Ok); Assert.False(w.Repo.ReadMainProfiles().ContainsKey("a")); Assert.False(File.Exists(marker)); Assert.True(w.Repo.ReadMainProfiles().ContainsKey("b"));
        Assert.True((await other.SyncTickAsync()).Ok); Assert.False(File.Exists(Path.Combine(otherStore,"a.json")));
    }
    [Fact] public async Task LastProfileCanBeExplicitlyDeleted()
    {
        using var w = new GitTestWorkspace(); w.Host("a"); await w.Engine.SyncTickAsync(); var marker = Delete(w,"a");
        var result = await w.Engine.SyncTickAsync(); Assert.True(result.Ok); Assert.Empty(result.Warnings); Assert.Empty(w.Repo.ReadMainProfiles()); Assert.False(File.Exists(marker));
    }
    [Fact] public async Task TombstoneConflictsWithHostEditAndHostResolutionClearsIt()
    {
        using var w = new GitTestWorkspace(); w.Host("a"); await w.Engine.SyncTickAsync(); var marker = Delete(w,"a"); w.Host("a",GitTestWorkspace.Profile("a","host"));
        var result = await w.Engine.SyncTickAsync(); Assert.True(result.Conflict); Assert.True(File.Exists(marker));
        await w.G("add","projects/a.json"); result = await w.Engine.SyncTickAsync();
        Assert.True(result.Ok); Assert.Contains("host",File.ReadAllText(Path.Combine(w.Store,"a.json"))); Assert.False(File.Exists(marker));
    }
    [Fact] public async Task HostProfileUnknownToVmWinsOverTombstone()
    {
        using var w = new GitTestWorkspace(); await w.Engine.SyncTickAsync(); w.Host("a"); var marker = Delete(w,"a");
        Assert.True((await w.Engine.SyncTickAsync()).Ok); Assert.True(File.Exists(Path.Combine(w.Store,"a.json"))); Assert.False(File.Exists(marker));
    }
    [Fact] public async Task StaleAndInvalidTombstonesDoNotDeleteProfiles()
    {
        using var w = new GitTestWorkspace(); w.Host("a"); await w.Engine.SyncTickAsync(); Delete(w,"a"); w.Vm("a");
        File.WriteAllText(Path.Combine(w.Store,".deleted","default"),""); File.WriteAllText(Path.Combine(w.Store,".deleted","bad..name"),"");
        var result = await w.Engine.SyncTickAsync(); Assert.True(result.Ok); Assert.True(w.Repo.ReadMainProfiles().ContainsKey("a")); Assert.Equal(2,result.Warnings.Count);
        Assert.False(File.Exists(Path.Combine(w.Store,".deleted","a"))); var main = await w.G("rev-parse","main");
        File.Delete(Path.Combine(w.Store,"a.json")); result = await w.Engine.SyncTickAsync();
        Assert.True(result.Ok); Assert.True(File.Exists(Path.Combine(w.Store,"a.json"))); Assert.Equal(main,await w.G("rev-parse","main"));
    }
    [Fact] public async Task ProfileNamedDeletedDoesNotCollideWithWireToken()
    {
        using var w = new GitTestWorkspace(); w.Vm("DELETED");
        var parsed = StoreScripts.ParseReadStore((await w.Ssh.RunRemoteScriptAsync(StoreScripts.BuildReadStoreScript(w.Store))).Stdout)!;
        Assert.Empty(parsed.Deleted); Assert.Equal("DELETED",Assert.Single(parsed.Entries).Name);
        Assert.True((await w.Engine.SyncTickAsync()).Ok); Assert.True(w.Repo.ReadMainProfiles().ContainsKey("DELETED"));
        Delete(w,"DELETED"); Assert.True((await w.Engine.SyncTickAsync()).Ok); Assert.False(w.Repo.ReadMainProfiles().ContainsKey("DELETED"));
    }
    [Theory] [InlineData(false)] [InlineData(true)] public async Task AbsentWriteSkipsConcurrentDelete(bool fresh)
    {
        using var w = new GitTestWorkspace(); w.Host("a"); w.Host("b");
        if (!fresh) { await w.Engine.SyncTickAsync(); File.Delete(Path.Combine(w.Store,"a.json")); }
        w.Ssh.BeforeRun = script => { if (script.Contains("mkdir -p",StringComparison.Ordinal)) Delete(w,"a"); return Task.CompletedTask; };
        var result = await w.Engine.SyncTickAsync(); Assert.True(result.Ok); Assert.Contains("a",result.WriteBack.Skipped); Assert.False(File.Exists(Path.Combine(w.Store,"a.json"))); Assert.True(File.Exists(Path.Combine(w.Store,".deleted","a")));
    }
    [Fact] public async Task ConcurrentVmEditPreventsCommonBaseAdvance()
    {
        using var w = new GitTestWorkspace(); w.Host("a"); await w.Engine.SyncTickAsync(); var old = await w.G("rev-parse","vm"); w.Host("a",GitTestWorkspace.Profile("a","host"));
        w.Ssh.BeforeRun = script => { if (script.Contains("mkdir -p",StringComparison.Ordinal)) w.Vm("a",GitTestWorkspace.Profile("a","racing")); return Task.CompletedTask; };
        var result = await w.Engine.SyncTickAsync(); Assert.True(result.Ok); Assert.Contains("a",result.WriteBack.Skipped); Assert.Equal(old,await w.G("rev-parse","vm"));
        w.Ssh.BeforeRun = null; Assert.True((await w.Engine.SyncTickAsync()).Conflict);
    }
    [Fact] public async Task MissingGitAndOfflineVmDegradeWithoutClaimingSync()
    {
        using var w = new GitTestWorkspace(); w.Host("a"); w.Ssh.Offline = true; var tick = await w.Engine.SyncTickAsync(); Assert.True(tick.Ok); Assert.False(tick.VmReadOk); Assert.False(File.Exists(Path.Combine(w.Repo.Directory,".sync.lock")));
    }
    [Fact] public async Task NondefaultVmHasSeparateHistory()
    {
        using var w = new GitTestWorkspace(); w.Host("a"); await w.Engine.SyncTickAsync(); var first = await w.G("rev-parse","vm");
        var other = new ConfigSyncEngine(w.Repo,w.Lock,w.Ssh,"vm-other",w.Store); w.Vm("a",GitTestWorkspace.Profile("a","other")); Assert.True((await other.SyncTickAsync()).Ok); Assert.Equal(first,await w.G("rev-parse","vm")); Assert.NotEqual(first,await w.G("rev-parse","vm-other"));
    }
    [Fact] public async Task DoesNotAdoptAncestorRepositoryOrReservedSeeds()
    {
        using var w = new GitTestWorkspace(); await w.Git.RequireAsync(w.Root,["init"]); w.Host("default"); w.Host("a");
        Assert.True((await w.Engine.SyncTickAsync()).Ok); Assert.Equal(w.Repo.Directory,await w.G("rev-parse","--show-toplevel")); Assert.DoesNotContain("default.json",await w.G("ls-files"));
    }
}
