using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.ConfigSync;
using Construct.Companion.Host.ConfigSync;
using Construct.Companion.Fakes;
namespace Construct.Companion.Tests.ConfigSync;
public sealed class RuntimeTests
{
    [Fact] public async Task PerInstanceThrottleAndWireState()
    {
        using var w=new GitTestWorkspace(); w.Host("a"); var remotes=new ConfigRemotes(w.Repo,w.Cache);
        await using var a=new ConfigSyncRuntime(w.Engine,w.Repo,remotes,w.Clock);
        Assert.Null((await a.BuildStateAsync()).LastSyncAt); Assert.NotNull(await a.TickAutoAsync()); Assert.Null(await a.TickAutoAsync());
        await using var b=new ConfigSyncRuntime(new(w.Repo,w.Lock,w.Ssh,"vm-b",w.Store),w.Repo,remotes,w.Clock); Assert.True(b.DueForAuto); Assert.Null((await b.BuildStateAsync()).LastResult);
        w.Clock.Advance(TimeSpan.FromMinutes(5)); Assert.True(a.DueForAuto); Assert.NotNull(await a.TickAutoAsync());
        var state=await a.BuildStateAsync(); Assert.Equal("ok",state.LastResult); Assert.Equal(300000,state.LastSyncAt); Assert.True(state.RepoReady);
        var doc=System.Text.Json.Nodes.JsonNode.Parse(ConfigSyncRules.Serialize(state))!.AsObject(); Assert.Equal(new[]{"gitPresent","repoReady","conflict","conflictFiles","mergeInProgress","lastSyncAt","lastResult","blockedReason","warnings","remotes"},doc.Select(p=>p.Key));
    }
    [Fact] public async Task SameInstanceCallsShareOneFollowup()
    {
        using var w=new GitTestWorkspace(); w.Host("a"); var remotes=new ConfigRemotes(w.Repo,w.Cache); await using var runtime=new ConfigSyncRuntime(w.Engine,w.Repo,remotes,w.Clock);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var reads=0;
        w.Ssh.BeforeRun=async script=> { if(script.Contains("for f in",StringComparison.Ordinal) && Interlocked.Increment(ref reads)==1) { entered.SetResult(); await release.Task; } };
        var first=runtime.SyncNowAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var second=runtime.SyncNowAsync(); var third=runtime.SyncNowAsync(); release.SetResult(); await Task.WhenAll(first,second,third); Assert.Equal(2,reads); Assert.Same(await second,await third);
    }
    [Fact] public async Task WatcherDebouncesAndIsDisposed()
    {
        using var w=new GitTestWorkspace(); w.Host("a"); var remotes=new ConfigRemotes(w.Repo,w.Cache); var runtime=new ConfigSyncRuntime(w.Engine,w.Repo,remotes,w.Clock); await runtime.SyncNowAsync(); runtime.StartWatching(); var before=w.Ssh.Calls;
        w.Host("a",GitTestWorkspace.Profile("a","watch")); await Task.Delay(50); w.Clock.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(30); Assert.Equal(before,w.Ssh.Calls);
        w.Clock.Advance(TimeSpan.FromSeconds(3)); for(var i=0;i<100 && w.Ssh.Calls==before;i++) await Task.Delay(10); Assert.True(w.Ssh.Calls>before); await runtime.DisposeAsync();
        var calls=w.Ssh.Calls; w.Host("a"); w.Clock.Advance(TimeSpan.FromSeconds(10)); await Task.Delay(30); Assert.Equal(calls,w.Ssh.Calls);
    }
    [Fact] public async Task BestEffortHardeningDoesNotBlockTick()
    {
        using var w=new GitTestWorkspace(); w.Host("a"); var runner=new RejectConfigRunner(w.Processes); var repo=new ConfigRepository(new(runner),w.Files,w.Files,w.Repo.Directory); var engine=new ConfigSyncEngine(repo,w.Lock,w.Ssh,storeRoot:w.Store);
        var tick=await engine.SyncTickAsync(); Assert.True(tick.Ok); Assert.True(tick.Ran); Assert.True(runner.Rejected>0);
    }
    [Fact] public async Task ReseedWithInvalidFileDoesNotWarnAboutExpectedSkip()
    {
        using var w=new GitTestWorkspace(); w.Host("a"); await w.Engine.SyncTickAsync(); w.Vm("a","half-written"); var tick=await w.Engine.SyncTickAsync(); Assert.True(tick.Ok); Assert.Contains("a",tick.WriteBack.Skipped); Assert.DoesNotContain(tick.Warnings,s=>s.Contains("write-back skipped",StringComparison.Ordinal));
    }
    [Fact] public async Task FactoryQueuesDifferentInstancesBehindActiveTick()
    {
        using var w=new GitTestWorkspace();
        var first=new FakeSshTransport(); var second=new FakeSshTransport();
        first.Spool.ExpectRun(StoreScripts.BuildReadStoreScript(),new ProcessResult(0,"STORE_ABSENT\nEND\n"));
        second.Spool.ExpectRun(StoreScripts.BuildReadStoreScript(),new ProcessResult(0,"STORE_ABSENT\nEND\n"));
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory=new ConfigSyncFactory(w.Files,w.Files,w.Processes,w.Clock,new FakePrompts(),new FakeClipboard());
        await using var a=factory.Create(w.Repo.Directory,w.Cache,new DelayedTransport(first,entered,release),"vm");
        await using var b=factory.Create(w.Repo.Directory,w.Cache,second,"vm-b");
        var tickA=a.Runtime.SyncNowAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var tickB=b.Runtime.SyncNowAsync();
        await Task.Delay(50); Assert.False(tickB.IsCompleted); Assert.Empty(second.Scripts); release.SetResult();
        Assert.True((await tickA).Ran); var resultB=await tickB; Assert.True(resultB.Ran); Assert.False(resultB.LockBusy); Assert.Single(second.Scripts);
    }
    private sealed class DelayedTransport(ISshTransport inner,TaskCompletionSource entered,TaskCompletionSource release) : ISshTransport
    {
        public async Task<ProcessResult> RunRemoteScriptAsync(string script,TimeSpan? timeout=null,CancellationToken cancellationToken=default) { entered.TrySetResult(); await release.Task.WaitAsync(cancellationToken); return await inner.RunRemoteScriptAsync(script,timeout,cancellationToken); }
        public IRunningProcess SpawnWatch(string script,CancellationToken cancellationToken=default)=>inner.SpawnWatch(script,cancellationToken);
        public IRunningProcess SpawnTunnel(TunnelSpec tunnel,CancellationToken cancellationToken=default)=>inner.SpawnTunnel(tunnel,cancellationToken);
        public Task<bool> ProbePortAsync(int port,string bindHost="127.0.0.1",CancellationToken cancellationToken=default)=>inner.ProbePortAsync(port,bindHost,cancellationToken);
    }
    private sealed class RejectConfigRunner(IProcessRunner inner) : IProcessRunner
    {
        public int Rejected { get; private set; }
        public Task<ProcessResult> RunAsync(ProcessInvocation invocation,CancellationToken cancellationToken=default)
        {
            if(invocation.Arguments.FirstOrDefault()=="config") { Rejected++; return Task.FromResult(new ProcessResult(1,Stderr:"config write failed")); }
            return inner.RunAsync(invocation,cancellationToken);
        }
        public IRunningProcess Start(ProcessInvocation invocation,CancellationToken cancellationToken=default)=>inner.Start(invocation,cancellationToken);
    }
}
