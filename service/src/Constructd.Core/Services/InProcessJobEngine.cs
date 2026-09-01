using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Core.Services;

/// <summary>
/// Runs jobs on the thread pool of this process and persists their state through an
/// <see cref="IJobStore"/> (in-memory for fake mode, SQLite for a real host). SSE subscribers are by
/// nature per-process, so they live in memory; job state does not.
///
/// Secrets: a job's <see cref="JobOutcome.OneTimeSecret"/> is kept in memory only and handed to the
/// first caller of <see cref="TakeOneTimeSecretAsync"/>. It is never written to the store, never
/// part of <see cref="Job.Result"/>, and gone after a restart.
/// </summary>
/// <param name="diagnostics">
/// Called when a job fails, with a <em>safe</em> description of the failure — never the exception
/// itself. A dependency's exception can carry a command line, an environment block or a credential in
/// its message, stack trace, <c>Data</c> or inner exceptions, so nothing outside this class ever sees
/// it: not job state, not the SSE stream, not the audit trail, and not the log.
/// </param>
public sealed class InProcessJobEngine(IClock clock, IJobStore store, Action<Job, string>? diagnostics = null)
    : IJobEngine, IDisposable
{
    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public async Task<Job> SubmitAsync(
        string kind,
        string? vmName,
        string owner,
        Func<IProgress<string>, CancellationToken, Task<JobOutcome>> work,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(work);

        var entry = new JobEntry(new Job(
            Id: Guid.NewGuid().ToString("n"),
            Kind: kind,
            VmName: vmName,
            Owner: owner,
            State: JobState.Queued,
            Progress: [],
            Result: null,
            Error: null,
            Created: clock.UtcNow,
            Finished: null));

        _jobs[entry.Snapshot.Id] = entry;
        await store.UpsertAsync(entry.Snapshot, cancellationToken).ConfigureAwait(false);

        _ = Task.Run(() => RunAsync(entry, work), CancellationToken.None);
        return entry.Snapshot;
    }

    public async Task<Job?> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (_jobs.TryGetValue(id, out var entry))
        {
            return entry.Snapshot;
        }

        // Not in this process's memory: it may predate a restart.
        return await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> CancelAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_jobs.TryGetValue(id, out var entry))
        {
            return Task.FromResult(false);
        }

        lock (entry.Gate)
        {
            if (entry.Finished || entry.Completing)
            {
                return Task.FromResult(false);
            }
        }

        entry.Cancellation.Cancel();
        return Task.FromResult(true);
    }

    public Task<string?> TakeOneTimeSecretAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_secrets.TryRemove(id, out var secret) ? secret : null);
    }

    public async IAsyncEnumerable<JobEvent> SubscribeAsync(
        string id,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(id, out var entry))
        {
            // A job from before a restart has no live stream; emit its stored state and finish.
            var stored = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (stored is not null)
            {
                foreach (var line in stored.Progress)
                {
                    yield return new JobEvent(JobEventKind.Progress, line, null);
                }

                yield return new JobEvent(JobEventKind.State, null, stored);
            }

            yield break;
        }

        var channel = Channel.CreateUnbounded<JobEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        lock (entry.Gate)
        {
            // Replay what already happened, then join the live stream — atomically, so nothing is
            // lost or duplicated between the replay and the subscription.
            foreach (var line in entry.Snapshot.Progress)
            {
                channel.Writer.TryWrite(new JobEvent(JobEventKind.Progress, line, null));
            }

            if (entry.Finished)
            {
                channel.Writer.TryWrite(new JobEvent(JobEventKind.State, null, entry.Snapshot));
                channel.Writer.TryComplete();
            }
            else
            {
                entry.Subscribers.Add(channel);
            }
        }

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            lock (entry.Gate)
            {
                entry.Subscribers.Remove(channel);
            }
        }
    }

    public void Dispose()
    {
        foreach (var entry in _jobs.Values)
        {
            entry.Cancellation.Dispose();
        }
    }

    private async Task RunAsync(JobEntry entry, Func<IProgress<string>, CancellationToken, Task<JobOutcome>> work)
    {
        await UpdateAsync(entry, job => job with { State = JobState.Running }).ConfigureAwait(false);
        var progress = new JobProgress(this, entry);

        try
        {
            var outcome = await work(progress, entry.Cancellation.Token).ConfigureAwait(false);

            await CompleteAsync(
                entry,
                job => job with
                {
                    State = JobState.Succeeded,
                    Result = outcome.Result,
                    Finished = clock.UtcNow,
                },
                // Published together with the terminal state, i.e. only once it is durable.
                outcome.OneTimeSecret).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
        {
            await CompleteAsync(entry, job => job with
            {
                State = JobState.Cancelled,
                Error = "cancelled",
                Finished = clock.UtcNow,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The exception object stops here: only its safe description travels on.
            var safe = SafeError.Describe(ex);
            diagnostics?.Invoke(entry.Snapshot, safe);

            await CompleteAsync(entry, job => job with
            {
                State = JobState.Failed,
                Error = safe,
                Finished = clock.UtcNow,
            }).ConfigureAwait(false);
        }
    }

    private Task UpdateAsync(JobEntry entry, Func<Job, Job> mutate)
    {
        Job snapshot;
        lock (entry.Gate)
        {
            entry.Snapshot = mutate(entry.Snapshot);
            snapshot = entry.Snapshot;
        }

        return EnqueueWriteAsync(entry, snapshot);
    }

    /// <summary>
    /// Finishes a job: writes the terminal state, and only then makes it visible.
    ///
    /// Nothing terminal is observable before the store has it — <see cref="GetAsync"/> keeps returning
    /// the running snapshot, subscribers keep waiting, and the one-time secret cannot be consumed. A
    /// crash inside this window therefore looks like a job that was interrupted (which the startup
    /// recovery pass marks failed), never like a completed job whose state was lost.
    /// </summary>
    private async Task CompleteAsync(JobEntry entry, Func<Job, Job> mutate, string? oneTimeSecret = null)
    {
        Job terminal;
        lock (entry.Gate)
        {
            // Stop accepting progress, but leave the visible snapshot as it is for now.
            entry.Completing = true;
            terminal = mutate(entry.Snapshot);
        }

        // The terminal write goes through the SAME per-job chain as the progress writes and is awaited
        // here: a progress write that is still in flight can otherwise land afterwards and revert the
        // stored state to "running" with no result.
        await EnqueueWriteAsync(entry, terminal).ConfigureAwait(false);

        var fault = entry.TakePersistenceFault();
        if (fault is not null)
        {
            // The job really did reach this state; say so, and say that it may not survive a restart.
            terminal = terminal with
            {
                Error = Combine(terminal.Error, $"state could not be persisted: {SafeError.Describe(fault)}"),
            };
        }

        lock (entry.Gate)
        {
            entry.Snapshot = terminal;
            entry.Finished = true;

            if (!string.IsNullOrEmpty(oneTimeSecret))
            {
                _secrets[terminal.Id] = oneTimeSecret;
            }

            foreach (var subscriber in entry.Subscribers)
            {
                subscriber.Writer.TryWrite(new JobEvent(JobEventKind.State, null, terminal));
                subscriber.Writer.TryComplete();
            }

            entry.Subscribers.Clear();
        }
    }

    private static string Combine(string? existing, string added) =>
        string.IsNullOrEmpty(existing) ? added : $"{existing}; {added}";

    /// <summary>
    /// Appends a store write to this job's write chain, so writes land in the order they were made.
    /// A failure is recorded on the entry instead of faulting the chain, so one bad write cannot stop
    /// the following ones — <see cref="CompleteAsync"/> then reports it.
    /// </summary>
    private Task EnqueueWriteAsync(JobEntry entry, Job snapshot)
    {
        lock (entry.Gate)
        {
            entry.Persistence = Chain(entry.Persistence);
            return entry.Persistence;
        }

        async Task Chain(Task previous)
        {
            await previous.ConfigureAwait(false);

            try
            {
                await store.UpsertAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                entry.RecordPersistenceFault(ex);
            }
        }
    }

    private void Report(JobEntry entry, string text)
    {
        var line = new JobProgressLine(clock.UtcNow, text);
        Job snapshot;

        lock (entry.Gate)
        {
            if (entry.Finished || entry.Completing)
            {
                return;
            }

            entry.Snapshot = entry.Snapshot with { Progress = [.. entry.Snapshot.Progress, line] };
            snapshot = entry.Snapshot;

            foreach (var subscriber in entry.Subscribers)
            {
                subscriber.Writer.TryWrite(new JobEvent(JobEventKind.Progress, line, null));
            }
        }

        // Progress is reported from the job's own thread, so the write is queued rather than awaited;
        // the chain keeps it ordered and completion waits for it.
        _ = EnqueueWriteAsync(entry, snapshot);
    }

    private sealed class JobEntry(Job snapshot)
    {
        public Lock Gate { get; } = new();

        public Job Snapshot { get; set; } = snapshot;

        /// <summary>The terminal state is being written; no more progress, not visible yet.</summary>
        public bool Completing { get; set; }

        /// <summary>The terminal state is durable and published.</summary>
        public bool Finished { get; set; }

        public List<Channel<JobEvent>> Subscribers { get; } = [];

        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>Serializes the store writes of this job so snapshots cannot land out of order.</summary>
        public Task Persistence { get; set; } = Task.CompletedTask;

        private Exception? _persistenceFault;

        public void RecordPersistenceFault(Exception fault) =>
            Interlocked.CompareExchange(ref _persistenceFault, fault, null);

        /// <summary>The first store failure seen for this job, if any.</summary>
        public Exception? TakePersistenceFault() => Volatile.Read(ref _persistenceFault);
    }

    /// <summary>
    /// Synchronous progress sink. <see cref="Progress{T}"/> is deliberately not used: it posts to the
    /// thread pool, which would reorder job progress lines.
    /// </summary>
    private sealed class JobProgress(InProcessJobEngine engine, JobEntry entry) : IProgress<string>
    {
        public void Report(string value) => engine.Report(entry, value);
    }
}
