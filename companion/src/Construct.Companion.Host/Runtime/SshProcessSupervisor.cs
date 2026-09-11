using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Forwards;

namespace Construct.Companion.Host.Runtime;

public sealed class SshProcessSupervisor(IClock clock) : IRuntimeProcesses
{
    public ISupervisedProcess Start(Func<CancellationToken, IRunningProcess> spawn, ProcessSupervisionOptions options,
        Action<SupervisedProcessState> changed, Func<string, CancellationToken, Task>? output = null)
        => new Session(clock, spawn, options, changed, output);

    private sealed class Session : ISupervisedProcess
    {
        private readonly IClock clock;
        private readonly CancellationTokenSource stop = new();
        private readonly TaskCompletionSource<bool> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task run;
        private SupervisedProcessState state = new("starting");
        public SupervisedProcessState State => Volatile.Read(ref state);
        public Task<bool> FirstAttempt => first.Task;
        public Task Completion => run;
        public Session(IClock clock, Func<CancellationToken, IRunningProcess> spawn, ProcessSupervisionOptions options,
            Action<SupervisedProcessState> changed, Func<string, CancellationToken, Task>? output)
        { this.clock = clock; run = RunAsync(spawn, options, changed, output); }
        private void Publish(SupervisedProcessState next, Action<SupervisedProcessState> changed)
        { Volatile.Write(ref state, next); try { changed(next); } catch { /* Observers cannot own child lifetime. */ } }
        private async Task RunAsync(Func<CancellationToken, IRunningProcess> spawn, ProcessSupervisionOptions options,
            Action<SupervisedProcessState> changed, Func<string, CancellationToken, Task>? output)
        {
            var token = stop.Token; var failures = 0;
            try
            {
                do
                {
                    token.ThrowIfCancellationRequested();
                    var started = clock.UtcNow; var settled = false;
                    IRunningProcess? child = null;
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                    Task stdout = Task.CompletedTask, stderr = Task.CompletedTask, heartbeat = Task.CompletedTask;
                    long lastOutput = started.UtcTicks;
                    try
                    {
                        Publish(new("starting", failures), changed);
                        child = spawn(attempt.Token);
                        stdout = DrainAsync(child.StandardOutput, async chunk =>
                        {
                            Interlocked.Exchange(ref lastOutput, clock.UtcNow.UtcTicks);
                            if (output is not null) await output(chunk, attempt.Token).ConfigureAwait(false);
                        }, attempt.Token);
                        stderr = DrainAsync(child.StandardError, _ => Task.CompletedTask, attempt.Token);
                        heartbeat = options.HeartbeatTimeout is { } timeout ? CheckHeartbeatAsync(timeout, () => Interlocked.Read(ref lastOutput), attempt.Token)
                            : Task.Delay(Timeout.Infinite, attempt.Token);
                        var settle = clock.DelayAsync(options.Settle, attempt.Token);
                        var ended = await Task.WhenAny(child.Completion, stdout, stderr, heartbeat, settle).ConfigureAwait(false);
                        // End-of-output alone may be normal for a tunnel; faults are not.
                        if (ended == stdout && stdout.IsCompletedSuccessfully || ended == stderr && stderr.IsCompletedSuccessfully)
                            ended = await Task.WhenAny(child.Completion, heartbeat, settle).ConfigureAwait(false);
                        if (ended == settle && !child.Completion.IsCompleted && !stdout.IsFaulted && !stderr.IsFaulted)
                        {
                            await settle.ConfigureAwait(false); token.ThrowIfCancellationRequested(); settled = true;
                            Publish(new("up", failures), changed); first.TrySetResult(true);
                            await Task.WhenAny(child.Completion, heartbeat, FaultOnly(stdout, attempt.Token), FaultOnly(stderr, attempt.Token)).ConfigureAwait(false);
                        }
                        token.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch { /* Diagnostics use fixed text: arbitrary SSH output may contain credentials. */ }
                    finally
                    {
                        await attempt.CancelAsync().ConfigureAwait(false);
                        if (child is not null)
                        {
                            try { await child.DisposeAsync().ConfigureAwait(false); } catch { }
                            try { await child.Completion.ConfigureAwait(false); } catch { }
                        }
                        await IgnoreAsync(stdout).ConfigureAwait(false); await IgnoreAsync(stderr).ConfigureAwait(false); await IgnoreAsync(heartbeat).ConfigureAwait(false);
                    }
                    token.ThrowIfCancellationRequested();
                    first.TrySetResult(false);
                    if (clock.UtcNow - started >= TimeSpan.FromSeconds(60)) failures = 0;
                    failures++;
                    Publish(new(!settled || failures > 5 ? "failed" : "starting", failures,
                        !settled ? "the SSH tunnel exited immediately" : "the SSH tunnel keeps dropping"), changed);
                    if (!options.Restart) break;
                    await clock.DelayAsync(TimeSpan.FromMilliseconds(ForwardProtocol.ReconnectDelayMs(failures)), token).ConfigureAwait(false);
                } while (true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally { first.TrySetResult(false); Publish(new("stopped", failures), changed); }
        }
        private async Task CheckHeartbeatAsync(TimeSpan timeout, Func<long> lastOutput, CancellationToken token)
        {
            while (true)
            {
                var remaining = timeout - (clock.UtcNow - new DateTimeOffset(lastOutput(), TimeSpan.Zero));
                if (remaining <= TimeSpan.Zero) return;
                await clock.DelayAsync(remaining, token).ConfigureAwait(false);
            }
        }
        private static async Task DrainAsync(IAsyncEnumerable<string> source, Func<string, Task> consume, CancellationToken token)
        { await foreach (var chunk in source.WithCancellation(token).ConfigureAwait(false)) await consume(chunk).ConfigureAwait(false); }
        private static async Task FaultOnly(Task task, CancellationToken token)
        { await task.ConfigureAwait(false); await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); }
        private static async Task IgnoreAsync(Task task) { try { await task.ConfigureAwait(false); } catch { } }
        public async ValueTask DisposeAsync() { await stop.CancelAsync().ConfigureAwait(false); await run.ConfigureAwait(false); }
    }
}
