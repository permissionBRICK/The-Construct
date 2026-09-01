using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

/// <summary>
/// Durable job state. Deliberately narrow: a job is small (a handful of progress lines plus a
/// result), so the engine writes whole snapshots instead of maintaining a delta protocol.
///
/// A store must never write a secret. <see cref="Job.Result"/> is already secret-free by contract;
/// the one-time secret of a job lives only in the engine's memory.
/// </summary>
public interface IJobStore
{
    Task UpsertAsync(Job job, CancellationToken cancellationToken);

    Task<Job?> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Startup recovery: a job that was still queued or running when the process ended cannot be
    /// resumed, so it is marked failed. Returns how many jobs were affected.
    /// </summary>
    Task<int> MarkInterruptedAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
