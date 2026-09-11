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
    [Fact] public async Task IndividualDeletionPropagates()
    {
        using var w = new GitTestWorkspace(); w.Host("a"); w.Host("b"); await w.Engine.SyncTickAsync(); File.Delete(Path.Combine(w.Store,"a.json"));
        Assert.True((await w.Engine.SyncTickAsync()).Ok); Assert.False(w.Repo.ReadMainProfiles().ContainsKey("a")); Assert.True(w.Repo.ReadMainProfiles().ContainsKey("b"));
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
