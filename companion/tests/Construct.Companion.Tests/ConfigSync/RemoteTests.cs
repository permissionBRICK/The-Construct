using System.IO.Compression;
using Construct.Companion.Core.ConfigSync;
using Construct.Companion.Fakes;
using Construct.Companion.Host.ConfigSync;
namespace Construct.Companion.Tests.ConfigSync;
public sealed class RemoteTests
{
    private static async Task<string> Bare(GitTestWorkspace w,string name="remote.git") { var dir=Path.Combine(w.Root,name); await w.Git.RequireAsync(w.Root,["init","--bare","-b","main",dir]); return dir; }
    private static async Task Identity(GitTestWorkspace w,string dir) { await w.Git.RequireAsync(dir,["config","user.name","Test Author"]); await w.Git.RequireAsync(dir,["config","user.email","test@example.invalid"]); }
    private static PublishFile FileA(string command="base") => new("a","projects/a.json",GitTestWorkspace.Profile("a",command),false);
    [Fact] public async Task PublishIntoEmptyRemoteVerifiesCommitAndBlobs()
    {
        using var w=new GitTestWorkspace(); var url=await Bare(w); var remote=new ConfigRemotes(w.Repo,w.Cache); var clone=await remote.EnsurePublishCloneAsync(url); Assert.True(clone.Ok); await Identity(w,clone.Dir);
        Assert.True((await remote.CheckoutPublishBranchAsync(clone.Dir,"main")).Ok); var pushed=await remote.PublishToRemoteAsync(clone.Dir,"main",[FileA()]); Assert.True(pushed.Ok,pushed.Output); Assert.Equal(40,pushed.Commit.Length); Assert.Equal(40,pushed.BlobShas["a"].Length);
        Assert.Equal(pushed.Commit,await w.Git.RequireAsync(w.Root,["--git-dir",url,"rev-parse","main"])); Assert.Equal("Test Author",await w.Git.RequireAsync(clone.Dir,["log","-1","--format=%an"]));
    }
    [Fact] public async Task FailedFirstPublishRemainsRetryableAndMarkerIsOpaque()
    {
        using var w=new GitTestWorkspace(); var url=Path.Combine(w.Root,"later.git"); var remote=new ConfigRemotes(w.Repo,w.Cache);
        var clone=await remote.EnsurePublishCloneAsync(url); Assert.True(clone.Created); Assert.Equal(ConfigRemotes.PendingSentinel,System.IO.File.ReadAllText(Path.Combine(clone.Dir,".git",ConfigRemotes.PendingMarker)));
        await Identity(w,clone.Dir); await remote.CheckoutPublishBranchAsync(clone.Dir,"main"); var failed=await remote.PublishToRemoteAsync(clone.Dir,"main",[FileA()]); Assert.False(failed.Ok);
        clone=await remote.EnsurePublishCloneAsync(url); Assert.True(clone.Ok); Assert.True(clone.Created);
        await Bare(w,"later.git"); clone=await remote.EnsurePublishCloneAsync(url); await remote.CheckoutPublishBranchAsync(clone.Dir,"main"); var retry=await remote.PublishToRemoteAsync(clone.Dir,"main",[FileA()]); Assert.True(retry.Ok,retry.Output); Assert.False(System.IO.File.Exists(Path.Combine(clone.Dir,".git",ConfigRemotes.PendingMarker)));
    }
    [Fact] public async Task UnreachableEstablishedCloneDoesNotBecomePushToCreate()
    {
        using var w=new GitTestWorkspace(); var url=await Bare(w); var remote=new ConfigRemotes(w.Repo,w.Cache); var clone=await remote.EnsurePublishCloneAsync(url); await Identity(w,clone.Dir); await remote.CheckoutPublishBranchAsync(clone.Dir,"main"); Assert.True((await remote.PublishToRemoteAsync(clone.Dir,"main",[FileA()])).Ok);
        Directory.Move(url,url+".offline"); var failed=await remote.EnsurePublishCloneAsync(url); Assert.False(failed.Ok); Assert.False(failed.Created);
    }
    [Fact] public async Task ImportCreatesMergesConflictsAndRenamesWithProvenance()
    {
        using var w=new GitTestWorkspace(); var remote=new ConfigRemotes(w.Repo,w.Cache); var prompts=new FakePrompts(); var actions=new ConfigSyncActions(w.Repo,remote,w.Lock,prompts,new FakeClipboard(),w.Clock);
        var selection=new ImportSelection("https://host/config","HEAD","projects/a.json","a",GitTestWorkspace.Profile("a")); Assert.Equal(1,(await actions.ImportSelectedAsync([selection])).Count);
        Assert.Equal(selection.Content,System.IO.File.ReadAllText(w.Repo.FilePath("bases","a"))); Assert.Equal(selection.RemoteUrl,remote.ReadImportManifest()["a"].RemoteUrl);
        var update=selection with { Content=GitTestWorkspace.Profile("a","upstream") }; Assert.Equal(1,(await actions.ImportSelectedAsync([update])).Count); Assert.Contains("upstream",w.Repo.ReadMainProfiles()["a"]);
        w.Host("a",GitTestWorkspace.Profile("a","local")); var conflict=await actions.ImportSelectedAsync([selection with { Content=GitTestWorkspace.Profile("a","diverged") }]); Assert.Equal(0,conflict.Count); Assert.Contains("local",w.Repo.ReadMainProfiles()["a"]);
        var other=selection with { RemoteUrl="https://host/other" }; var renamed=await actions.ImportSelectedAsync([other],new Dictionary<string,string>{{"a","a-2"}}); Assert.Equal(1,renamed.Count); Assert.Equal("a-2",remote.ReadImportManifest()["a-2"].ImportedAs); Assert.Equal("projects/a.json",remote.ReadImportManifest()["a-2"].PathInRemote);
    }
    [Fact] public async Task PublishFlowAdoptsOnlyAfterPushAndKeepsTrackedProfilesOut()
    {
        using var w=new GitTestWorkspace(); var url=await Bare(w); var remote=new ConfigRemotes(w.Repo,w.Cache); remote.WriteRemotes([new(url)]); w.Host("a");
        var clone=await remote.EnsurePublishCloneAsync(url); await Identity(w,clone.Dir); var actions=new ConfigSyncActions(w.Repo,remote,w.Lock,new FakePrompts(),new FakeClipboard(),w.Clock);
        var result=await actions.PublishSelectedAsync(url,["a"]); Assert.True(result.Ok,result.Message); var entry=remote.ReadImportManifest()["a"]; Assert.Equal(40,entry.BaseCommit!.Length); Assert.Equal(40,entry.BaseBlobSha!.Length); Assert.Equal(w.Repo.ReadMainProfiles()["a"],System.IO.File.ReadAllText(w.Repo.FilePath("bases","a")));
        Assert.False((await actions.PublishSelectedAsync(url,["a"])).Ok);
    }
    [Fact] public async Task PublishFailureDoesNotAdoptAndRejectsRemoteConflict()
    {
        using var w=new GitTestWorkspace(); var url=Path.Combine(w.Root,"missing.git"); var remote=new ConfigRemotes(w.Repo,w.Cache); var clone=await remote.EnsurePublishCloneAsync(url); await Identity(w,clone.Dir); w.Host("a");
        var actions=new ConfigSyncActions(w.Repo,remote,w.Lock,new FakePrompts(),new FakeClipboard(),w.Clock); Assert.False((await actions.PublishSelectedAsync(url,["a"])).Ok); Assert.Empty(remote.ReadImportManifest());
    }
    [Fact] public async Task StagingRefreshAndPushBackUseReviewBranch()
    {
        using var w=new GitTestWorkspace(); var url=await Bare(w); var remote=new ConfigRemotes(w.Repo,w.Cache); var clone=await remote.EnsurePublishCloneAsync(url); await Identity(w,clone.Dir); await remote.CheckoutPublishBranchAsync(clone.Dir,"main"); await remote.PublishToRemoteAsync(clone.Dir,"main",[FileA()]);
        await w.Git.RequireAsync(clone.Dir,["fetch","origin"]); await w.Git.RequireAsync(clone.Dir,["remote","set-head","origin","main"]);
        Assert.True((await remote.EnsureStagingCloneAsync(url)).Ok); w.Host("a",GitTestWorkspace.Profile("a","pushback"));
        var pushed=await remote.PushUpstreamAsync(clone.Dir,[new(w.Repo.FilePath("projects","a"),"projects/a.json")],"construct-config-update-test"); Assert.True(pushed.Ok,pushed.Output);
        Assert.Contains("pushback",await w.Git.RequireAsync(w.Root,["--git-dir",url,"show","construct-config-update-test:projects/a.json"])); Assert.DoesNotContain("pushback",await w.Git.RequireAsync(w.Root,["--git-dir",url,"show","main:projects/a.json"]));
    }
    [Theory] [InlineData("../../outside")] [InlineData("projects/../../outside")] [InlineData(".git/config")] public void PushPathsCannotEscapeClone(string path) => Assert.Throws<ConfigSyncException>(()=>ConfigRemotes.ContainedPath("/staging",path));
    [Fact] public async Task ShareProducesClipboardCommandOrZipThroughSeams()
    {
        using var w=new GitTestWorkspace(); w.Host("a"); var remote=new ConfigRemotes(w.Repo,w.Cache); var prompts=new FakePrompts(); var clipboard=new FakeClipboard(); var actions=new ConfigSyncActions(w.Repo,remote,w.Lock,prompts,clipboard,w.Clock);
        prompts.Picks.Enqueue(["a"]); prompts.SaveFiles.Enqueue(Path.Combine(w.Root,"bundle.zip")); var shared=await actions.ShareAsync(); Assert.True(shared.Ok);
        using(var zip=ZipFile.OpenRead(Path.Combine(w.Root,"bundle.zip"))) { Assert.Equal(new[]{"deploy.ps1","projects/a.json"},zip.Entries.Select(e=>e.FullName)); using var reader=new StreamReader(zip.GetEntry("deploy.ps1")!.Open()); Assert.Equal(ConfigSharing.BuildDeployPs1(),await reader.ReadToEndAsync()); }
        remote.Adopt("a",GitTestWorkspace.Profile("a"),new("https://host/config","main","projects/a.json","a"),GitTestWorkspace.Profile("a")); prompts.Picks.Enqueue(["a"]); Assert.True((await actions.ShareAsync()).Ok); Assert.Equal(ConfigSharing.BuildShareCommand("https://host/config",["a"]),clipboard.Text);
    }
}
