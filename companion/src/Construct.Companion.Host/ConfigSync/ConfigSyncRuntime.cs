using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.ConfigSync;

namespace Construct.Companion.Host.ConfigSync;

// One runtime per captured instance. The shared disk lock serializes different instances.
public sealed class ConfigSyncRuntime : IAsyncDisposable
{
    private readonly SemaphoreSlim repositoryQueue; private Task<GitPresence>? gitDetection; private DateTimeOffset gitDetectedAt;
    private readonly ConfigSyncEngine engine; private readonly ConfigRepository repo; private readonly ConfigRemotes remotes; private readonly IClock clock;
    private readonly object gate = new(); private readonly CancellationTokenSource lifetime = new();
    private TaskCompletionSource<SyncResult>? active; private TaskCompletionSource<SyncResult>? followup; private Task drain = Task.CompletedTask;
    private readonly SemaphoreSlim watchSignal = new(0,1); private IDisposable? watch; private Task watching = Task.CompletedTask;
    private long changedAt; private long? lastAt; private SyncResult? last; private bool disposed;
    public ConfigSyncRuntime(ConfigSyncEngine engine, ConfigRepository repo, ConfigRemotes remotes, IClock clock, SemaphoreSlim repositoryQueue) { this.engine=engine; this.repo=repo; this.remotes=remotes; this.clock=clock; this.repositoryQueue=repositoryQueue; }
    public bool DueForAuto { get { lock(gate) return lastAt == null || clock.UtcNow.ToUnixTimeMilliseconds()-lastAt >= 300000; } }
    public async Task<SyncResult?> TickAutoAsync(CancellationToken ct = default) => DueForAuto ? await SyncNowAsync(ct) : null;
    public Task<SyncResult> SyncNowAsync(CancellationToken ct = default)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if (active != null) { followup ??= new(TaskCreationOptions.RunContinuationsAsynchronously); return followup.Task.WaitAsync(ct); }
            active = new(TaskCreationOptions.RunContinuationsAsynchronously); var task = active.Task; drain = DrainAsync(); return task.WaitAsync(ct);
        }
    }
    private async Task DrainAsync()
    {
        // Yield so assignment of drain and active is complete before a synchronous fake finishes.
        await Task.Yield();
        while (true)
        {
            TaskCompletionSource<SyncResult> completion; lock(gate) completion = active!;
            try
            {
                await repositoryQueue.WaitAsync(lifetime.Token);
                SyncResult result;
                try { result = await engine.SyncTickAsync(lifetime.Token); }
                finally { repositoryQueue.Release(); }
                lock(gate) { last=result; lastAt=clock.UtcNow.ToUnixTimeMilliseconds(); }
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception) { completion.TrySetException(new ConfigSyncException("Config sync failed.")); } // git output may carry paths or remotes: fixed text outward
            lock(gate)
            {
                active=followup; followup=null;
                if (disposed) { active?.TrySetCanceled(); active=null; }
                if (active == null) return;
            }
        }
    }
    public void StartWatching()
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this); if (watch != null) return; repo.EnsureConfigTree();
            watch=repo.Files.Watch(Path.Combine(repo.Directory,"projects"),()=> { Interlocked.Exchange(ref changedAt,clock.UtcNow.ToUnixTimeMilliseconds()); try { watchSignal.Release(); } catch (SemaphoreFullException) { } });
            watching=WatchLoop();
        }
    }
    private async Task WatchLoop()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await watchSignal.WaitAsync(lifetime.Token);
                while (true)
                {
                    var remaining=Interlocked.Read(ref changedAt)+2000-clock.UtcNow.ToUnixTimeMilliseconds();
                    if (remaining<=0) break;
                    await clock.DelayAsync(TimeSpan.FromMilliseconds(remaining),lifetime.Token);
                }
                while (watchSignal.Wait(0)) { }
                await SyncNowAsync(lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (disposed) { }
    }
    public async Task<ConfigSyncState> BuildStateAsync(CancellationToken ct = default)
    {
        Task<GitPresence> detection;
        lock(gate)
        {
            if(gitDetection==null || clock.UtcNow-gitDetectedAt>=TimeSpan.FromMinutes(5))
            {
                gitDetectedAt=clock.UtcNow; gitDetection=repo.Git.DetectAsync(repo.Files.GetRoot(FileSystemRoot.Temp) ?? repo.Directory,lifetime.Token);
            }
            detection=gitDetection;
        }
        var git=await detection.WaitAsync(ct); var state=git.Present ? await repo.StateAsync(ct) : new RepoState(false,false,[],false);
        if (state.MergeInProgress && !state.Conflict)
        {
            bool idle; lock(gate) idle=active == null;
            if (idle) { await SyncNowAsync(ct); state=await repo.StateAsync(ct); }
        }
        lock(gate) return new(git.Present,state.Repo,state.Conflict,state.ConflictFiles,state.MergeInProgress,lastAt,
            last == null ? null : last.Ok ? "ok" : last.Conflict ? "conflict" : last.Blocked ? "blocked" : "error",last?.BlockedReason,last?.Warnings.ToArray() ?? [],git.Present ? remotes.ReadRemotes().Select(r=>new ConfigRemote(ConfigSyncRules.DisplayRemoteUrl(r.Url))).ToArray() : []);
    }
    public async Task<bool> LifecycleBlockedAsync(string branch, CancellationToken ct)
    {
        await repositoryQueue.WaitAsync(ct);
        try
        {
            await repo.CompletePendingMergeAsync(branch, ct);
            var state = await repo.StateAsync(ct);
            return state.Conflict || state.MergeInProgress;
        }
        finally { repositoryQueue.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        lock(gate) { if (disposed) return; disposed=true; watch?.Dispose(); watch=null; }
        await lifetime.CancelAsync(); await Task.WhenAll(watching,drain); lifetime.Dispose(); watchSignal.Dispose();
    }
}
public sealed record ConfigSyncState(bool GitPresent, bool RepoReady, bool Conflict, IReadOnlyList<string> ConflictFiles, bool MergeInProgress,
    long? LastSyncAt, string? LastResult, string? BlockedReason, IReadOnlyList<string> Warnings, IReadOnlyList<ConfigRemote> Remotes);
