using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.ConfigSync;
using Construct.Companion.Fakes;
using Construct.Companion.Host.ConfigSync;
namespace Construct.Companion.Tests.ConfigSync;
public sealed class ArgvTests
{
    private static (ConfigRemotes Remote,FakeProcessRunner Runner,FakeFileSystem Files) Setup()
    {
        var files=new FakeFileSystem(); var clock=new FakeClock(); var runner=new FakeProcessRunner(); var repo=new ConfigRepository(new(runner),files,new FakeConfigSyncStorage(files,clock),"/config"); return (new(repo,"/cache"),runner,files);
    }
    [Fact] public async Task PublishPinsUserIdentityCommitAndDefaultBranchPush()
    {
        var (remote,runner,_)=Setup(); runner.Results.Enqueue(new(0)); runner.Results.Enqueue(new(0,"projects/a.json\n")); runner.Results.Enqueue(new(0)); runner.Results.Enqueue(new(0)); runner.Results.Enqueue(new(0,new string('a',40))); runner.Results.Enqueue(new(0,new string('b',40)));
        var result=await remote.PublishToRemoteAsync("/cache/clone","main",[new("a","ignored",GitTestWorkspace.Profile("a"),false)]); Assert.True(result.Ok);
        Assert.Equal(new[]{new[]{"-c","core.hooksPath=","add","-A"},new[]{"diff","--cached","--name-only"},new[]{"-c","core.hooksPath=","commit","-m","publish 1 profiles"},new[]{"push","origin","HEAD:refs/heads/main"},new[]{"rev-parse","HEAD"},new[]{"rev-parse","HEAD:projects/a.json"}},runner.Invocations.Select(i=>i.Arguments.ToArray()).ToArray());
        Assert.All(runner.Invocations,i=>Assert.Equal("/cache/clone",i.WorkingDirectory)); Assert.Equal(TimeSpan.FromSeconds(60),runner.Invocations[3].Timeout);
    }
    [Fact] public async Task PushToCreatePinsCloneInitAndRemoteArgv()
    {
        var (remote,runner,files)=Setup(); runner.Results.Enqueue(new(1)); runner.Results.Enqueue(new(0)); runner.Results.Enqueue(new(0));
        var result=await remote.EnsurePublishCloneAsync("https://host/config.git"); Assert.True(result.Created);
        Assert.Equal(new[]{new[]{"clone","https://host/config.git","https---host-config.git"},new[]{"init"},new[]{"remote","add","origin","https://host/config.git"}},runner.Invocations.Select(i=>i.Arguments.ToArray()).ToArray());
        Assert.Equal(ConfigRemotes.PendingSentinel,System.Text.Encoding.UTF8.GetString(files.ReadFile(Path.Combine(result.Dir,".git",ConfigRemotes.PendingMarker))!));
    }
    [Fact] public async Task FailedAddCannotReportPublishSuccessOrPush()
    {
        var (remote,runner,_)=Setup(); runner.Results.Enqueue(new(1,Stderr:"add failed")); var result=await remote.PublishToRemoteAsync("/cache/clone","main",[new("a","",GitTestWorkspace.Profile("a"),false)]); Assert.False(result.Ok); Assert.Single(runner.Invocations); Assert.Empty(result.BlobShas);
    }
    [Fact] public async Task StartedProcessCanBeStoppedAndDisposedRepeatedly()
    {
        var process=new ConfigSyncProcessRunner().Start(new("sleep",["5"])); await process.StopAsync(); await process.StopAsync(); await process.DisposeAsync(); await process.DisposeAsync(); await process.StopAsync(); Assert.Equal(-1,await process.Completion);
    }
}
