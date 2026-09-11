using Construct.Companion.Core.Abstractions;
using Construct.Companion.Fakes;
using Construct.Companion.Host.ConfigSync;
using Construct.Companion.Host.Runtime;
namespace Construct.Companion.Tests.ConfigSync;
public sealed class SeamTests
{
    [Fact] public async Task GitPinsArgvIdentityIndexAndTimeout()
    {
        var process = new FakeProcessRunner(); var git = new GitRunner(process);
        await git.RunAsync("/repo",["commit-tree","tree","-p","parent","-m","vm sync"],true,"/repo/.git/tmp-vm-index");
        var i = Assert.Single(process.Invocations); Assert.Equal("git",i.FileName); Assert.Equal("/repo",i.WorkingDirectory); Assert.Equal(TimeSpan.FromSeconds(30),i.Timeout);
        Assert.Equal(GitRunner.Identity.Concat(new[]{"commit-tree","tree","-p","parent","-m","vm sync"}),i.Arguments); Assert.Equal("/repo/.git/tmp-vm-index",i.EnvironmentOverrides!["GIT_INDEX_FILE"]);
        Assert.DoesNotContain("tmp-vm-index",i.ToString());
    }
    [Fact] public async Task SecretUrlNeverReachesRunner()
    {
        var process = new FakeProcessRunner(); var result = await new GitRunner(process).RunAsync("/repo",["clone","https://user:fixture-secret@host/x","target"]); Assert.NotEqual(0,result.Code); Assert.Empty(process.Invocations); Assert.DoesNotContain("fixture-secret",result.Stderr);
    }
    [Fact] public void DeadOwnerAndStaleLocksAreBrokenAndTokensProtectSuccessor()
    {
        var files = new FakeFileSystem(); var clock = new FakeClock(); var storage = new FakeProcessLiveness(); files.CreateDirectory("/config"); var sync = new SyncLock(files,storage,clock,"/config");
        var old = sync.Acquire(); Assert.NotNull(old); Assert.Null(sync.Acquire()); clock.Advance(TimeSpan.FromMinutes(6)); var newer = sync.Acquire(); Assert.NotNull(newer); sync.Release(old); Assert.True(files.FileExists("/config/.sync.lock")); sync.Release(newer); Assert.False(files.FileExists("/config/.sync.lock"));
        old = sync.Acquire(); storage.DeadProcesses.Add(storage.ProcessId); Assert.NotNull(sync.Acquire()); sync.Release(old); Assert.True(files.FileExists("/config/.sync.lock"));
    }
    [Fact] public void ProvisionIntentYieldsUntilDeadOrExpired()
    {
        var files = new FakeFileSystem(); var clock = new FakeClock(); var storage = new FakeProcessLiveness(); var sync = new SyncLock(files,storage,clock,"/config"); files.WriteFileAtomic("/config/.sync.provisioning","{\"pid\":88}"u8); Assert.True(sync.ProvisionSyncPending()); storage.DeadProcesses.Add(88); Assert.False(sync.ProvisionSyncPending());
    }
    [Fact] public void FreshLockAndIntentAgeFromTheirOwnWriteTime()
    {
        var clock = new FakeClock(); var files = new FakeFileSystem(clock); var storage = new FakeProcessLiveness(); var sync = new SyncLock(files,storage,clock,"/config");
        clock.Advance(TimeSpan.FromMinutes(6)); var token = sync.Acquire(); Assert.NotNull(token); Assert.Null(sync.Acquire());
        clock.Advance(TimeSpan.FromMinutes(4)); Assert.Null(sync.Acquire()); clock.Advance(TimeSpan.FromMinutes(2)); Assert.NotNull(sync.Acquire());
        files.WriteFileAtomic("/config/.sync.provisioning","{\"pid\":88}"u8); Assert.True(sync.ProvisionSyncPending());
        clock.Advance(TimeSpan.FromMinutes(4)); Assert.True(sync.ProvisionSyncPending()); clock.Advance(TimeSpan.FromMinutes(2)); Assert.False(sync.ProvisionSyncPending());
    }
    [Fact] public async Task RealProcessCapturesArgvAndStopsOnTimeout()
    {
        var runner = new RuntimeProcessRunner(); var output = await runner.RunAsync(new("printf",["%s","literal $(id) 'quoted'"])); Assert.Equal("literal $(id) 'quoted'",output.Stdout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(new("sleep",["5"],Timeout:TimeSpan.FromMilliseconds(30))));
    }
}
