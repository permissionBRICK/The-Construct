using Construct.Companion.Core.Abstractions;
using Construct.Companion.Fakes;
using Construct.Companion.Host.ConfigSync;
namespace Construct.Companion.Tests.ConfigSync;
public sealed class NetworkLockTests
{
    [Fact] public async Task BlockedImportCloneDoesNotHoldProvisioningLock()
    {
        using var w=new GitTestWorkspace(); var url=Path.Combine(w.Root,"remote.git"); await w.Git.RequireAsync(w.Root,["init","--bare","-b","main",url]);
        var runner=new BlockingNetworkRunner(w.Processes); var repo=new ConfigRepository(new(runner),w.Files,w.Files,w.Repo.Directory); var remote=new ConfigRemotes(repo,w.Cache); remote.WriteRemotes([new(url)]);
        var actions=new ConfigSyncActions(repo,remote,w.Lock,new FakePrompts(),new FakeClipboard(),w.Clock); var import=actions.ImportAsync();
        await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var other=new SyncLock(w.Files,w.Files,w.Clock,repo.Directory); string? token=null;
        try { token=other.Acquire(); if(token!=null) other.Release(token); }
        finally { runner.Release.TrySetResult(); await import; }
        Assert.NotNull(token); Assert.False(System.IO.File.Exists(Path.Combine(repo.Directory,SyncLock.LockFile)));
    }
    private sealed class BlockingNetworkRunner(IProcessRunner inner) : IProcessRunner
    {
        public TaskCompletionSource Entered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProcessResult> RunAsync(ProcessInvocation invocation,CancellationToken cancellationToken=default)
        {
            if(invocation.Arguments.FirstOrDefault()=="clone") { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return await inner.RunAsync(invocation,cancellationToken);
        }
        public IRunningProcess Start(ProcessInvocation invocation,CancellationToken cancellationToken=default)=>inner.Start(invocation,cancellationToken);
    }
}
