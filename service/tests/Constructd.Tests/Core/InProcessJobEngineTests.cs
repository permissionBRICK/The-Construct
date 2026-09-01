using System.Collections.Concurrent;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Endpoint = Constructd.Core.Domain.Endpoint;
using Constructd.Core.Services;
using Constructd.Fakes;

namespace Constructd.Tests.Core;

/// <summary>
/// The job engine's durability contract: state changes reach the store in the order they happened, and
/// the terminal snapshot is the last thing written.
/// </summary>
public class InProcessJobEngineTests
{
    /// <summary>A store that can be told to hold a write, and that records everything it was given.</summary>
    private sealed class GatedJobStore : IJobStore
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);

        public List<Job> Writes { get; } = [];

        /// <summary>Writes whose progress contains this text block until <see cref="Release"/>.</summary>
        public string? HoldWritesContaining { get; set; }

        /// <summary>Writes carrying this state block until <see cref="Release"/>.</summary>
        public JobState? HoldWritesWithState { get; set; }

        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? Failure { get; set; }

        public void Release() => _gate.TrySetResult();

        public async Task UpsertAsync(Job job, CancellationToken cancellationToken)
        {
            if ((HoldWritesContaining is { } text && job.Progress.Any(line => line.Text.Contains(text))) ||
                HoldWritesWithState == job.State)
            {
                Held.TrySetResult();
                await _gate.Task.ConfigureAwait(false);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            lock (Writes)
            {
                Writes.Add(job);
            }

            _jobs[job.Id] = job;
        }

        public Task<Job?> GetAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(_jobs.TryGetValue(id, out var job) ? job : null);

        public Task<int> MarkInterruptedAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    [Fact]
    public async Task A_delayed_progress_write_cannot_overwrite_the_terminal_state()
    {
        var store = new GatedJobStore { HoldWritesContaining = "first" };
        using var engine = new InProcessJobEngine(new MutableClock(), store);

        var job = await engine.SubmitAsync("test", "work-vm", "bob", async (progress, _) =>
        {
            progress.Report("first");
            progress.Report("second");
            await Task.Yield();
            return new JobOutcome(new VmRemoveResult("work-vm", 2));
        }, CancellationToken.None);

        // The job has finished its work, but its first progress write is still in flight.
        await store.Held.Task;
        store.Release();

        var stream = engine.SubscribeAsync(job.Id, CancellationToken.None);
        JobEvent? terminal = null;
        await foreach (var evt in stream)
        {
            terminal = evt;
        }

        Assert.Equal(JobEventKind.State, terminal!.Kind);
        Assert.Equal(JobState.Succeeded, terminal.Job!.State);

        // The last thing the store saw is the terminal snapshot, with the result and all the progress.
        var stored = await store.GetAsync(job.Id, CancellationToken.None);
        Assert.Equal(JobState.Succeeded, stored!.State);
        Assert.NotNull(stored.Result);
        Assert.Equal(new[] { "first", "second" }, stored.Progress.Select(l => l.Text).ToArray());

        lock (store.Writes)
        {
            Assert.Equal(JobState.Succeeded, store.Writes[^1].State);
            Assert.Equal(2, store.Writes[^1].Progress.Count);
        }
    }

    /// <remarks>
    /// Nothing terminal may be observable before the store has the terminal state: not the job's state,
    /// not a subscriber's state event, and above all not the one-time secret. A crash inside that window
    /// has to look like an interrupted job, not like a completed one whose state was lost.
    /// </remarks>
    [Fact]
    public async Task Nothing_terminal_is_observable_until_the_terminal_write_lands()
    {
        var store = new GatedJobStore { HoldWritesWithState = JobState.Succeeded };
        using var engine = new InProcessJobEngine(new MutableClock(), store);

        var job = await engine.SubmitAsync("test", "work-vm", "bob",
            (progress, _) =>
            {
                progress.Report("working");
                return Task.FromResult(new JobOutcome(new VmCreateResult("work-vm", new Endpoint("h", 2201)), "TOKEN"));
            },
            CancellationToken.None);

        // A subscriber that attaches while the terminal write is blocked must not be completed.
        var subscription = Task.Run(async () =>
        {
            var events = new List<JobEvent>();
            await foreach (var evt in engine.SubscribeAsync(job.Id, CancellationToken.None))
            {
                events.Add(evt);
            }

            return events;
        });

        await store.Held.Task;

        var duringWrite = await engine.GetAsync(job.Id, CancellationToken.None);
        Assert.NotEqual(JobState.Succeeded, duringWrite!.State);
        Assert.Null(duringWrite.Result);
        Assert.Null(await engine.TakeOneTimeSecretAsync(job.Id, CancellationToken.None));
        Assert.False(subscription.IsCompleted);

        store.Release();

        var streamed = await subscription;
        Assert.Equal(JobEventKind.State, streamed[^1].Kind);
        Assert.Equal(JobState.Succeeded, streamed[^1].Job!.State);
        Assert.Equal("TOKEN", await engine.TakeOneTimeSecretAsync(job.Id, CancellationToken.None));
        Assert.Null(await engine.TakeOneTimeSecretAsync(job.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_store_failure_is_reported_on_the_job_rather_than_swallowed()
    {
        var store = new GatedJobStore();
        using var engine = new InProcessJobEngine(new MutableClock(), store);

        var job = await engine.SubmitAsync("test", null, "bob", (progress, _) =>
        {
            store.Failure = new IOException("disk is full");
            progress.Report("working");
            return Task.FromResult(new JobOutcome(Result: null));
        }, CancellationToken.None);

        Job? terminal = null;
        await foreach (var evt in engine.SubscribeAsync(job.Id, CancellationToken.None))
        {
            terminal = evt.Job ?? terminal;
        }

        Assert.Equal(JobState.Succeeded, terminal!.State);
        Assert.Contains("could not be persisted", terminal.Error);

        // The store's own message could contain anything; only its type is repeated.
        Assert.Contains("IOException", terminal.Error);
        Assert.DoesNotContain("disk is full", terminal.Error);
    }

    [Fact]
    public async Task A_job_from_before_a_restart_is_readable_from_the_store()
    {
        var store = new InMemoryJobStore();
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        await store.UpsertAsync(
            new Job("old-job", JobKinds.CreateVm, "work-vm", "bob", JobState.Succeeded,
                [new JobProgressLine(now, "done")], null, null, now, now),
            CancellationToken.None);

        using var engine = new InProcessJobEngine(new MutableClock(now), store);

        var job = await engine.GetAsync("old-job", CancellationToken.None);
        Assert.Equal(JobState.Succeeded, job!.State);

        // Its stream replays the stored log and ends with the stored state, without a live subscription.
        var events = new List<JobEvent>();
        await foreach (var evt in engine.SubscribeAsync("old-job", CancellationToken.None))
        {
            events.Add(evt);
        }

        Assert.Equal(JobEventKind.Progress, events[0].Kind);
        Assert.Equal(JobEventKind.State, events[^1].Kind);
    }

    [Fact]
    public async Task A_cancelled_job_ends_cancelled()
    {
        var store = new InMemoryJobStore();
        using var engine = new InProcessJobEngine(new MutableClock(), store);

        var started = new TaskCompletionSource();
        var job = await engine.SubmitAsync("test", null, "bob", async (progress, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return new JobOutcome(null);
        }, CancellationToken.None);

        await started.Task;
        Assert.True(await engine.CancelAsync(job.Id, CancellationToken.None));

        Job? terminal = null;
        await foreach (var evt in engine.SubscribeAsync(job.Id, CancellationToken.None))
        {
            terminal = evt.Job ?? terminal;
        }

        Assert.Equal(JobState.Cancelled, terminal!.State);
        Assert.False(await engine.CancelAsync(job.Id, CancellationToken.None));
    }
}
