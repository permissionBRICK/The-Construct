using System.Collections.Concurrent;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Fakes;

/// <summary>
/// Job state for fake mode: the same <see cref="Core.Services.InProcessJobEngine"/> runs on top of
/// this or of the SQLite store, so the engine itself is exercised identically either way.
/// </summary>
public sealed partial class InMemoryJobStore : IJobStore, IJobQueryStore
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);

    public Task UpsertAsync(Job job, CancellationToken cancellationToken)
    {
        lock (InMemoryTransaction.Gate)
        {
            ArgumentNullException.ThrowIfNull(job);
            cancellationToken.ThrowIfCancellationRequested();
            _jobs[job.Id] = job;
            return Task.CompletedTask;

        }
    }

    public Task<Job?> GetAsync(string id, CancellationToken cancellationToken)
    {
        lock (InMemoryTransaction.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_jobs.TryGetValue(id, out var job) ? job : null);

        }
    }

    public Task<int> MarkInterruptedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (InMemoryTransaction.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var affected = 0;
            foreach (var job in _jobs.Values.Where(j => j.State is JobState.Queued or JobState.Running))
            {
                _jobs[job.Id] = job with
                {
                    State = JobState.Failed,
                    Error = "interrupted by a service restart",
                    Finished = now,
                };
                affected++;
            }

            return Task.FromResult(affected);

        }
    }
    public Task<IReadOnlyList<Job>> ListAsync(CancellationToken ct) { lock (InMemoryTransaction.Gate) { return Task.FromResult<IReadOnlyList<Job>>(_jobs.Values.OrderByDescending(j => j.Created).ToArray()); } }
}
