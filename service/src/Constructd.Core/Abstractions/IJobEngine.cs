using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

/// <summary>
/// Runs long operations in the background: <c>POST → 202 + jobId</c>, progress streamed over
/// <c>GET /jobs/{id}/events</c> (plan §4.4). Implementations persist job state; the in-memory fake
/// keeps it for the lifetime of the process.
/// </summary>
public interface IJobEngine
{
    /// <summary>
    /// Queues <paramref name="work"/> and returns the queued job. <paramref name="owner"/> is the
    /// submitting principal and is what authorizes reads of the job and its result. Awaited because
    /// the job is durable from the moment it is queued.
    /// </summary>
    Task<Job> SubmitAsync(
        string kind,
        string? vmName,
        string owner,
        Func<IProgress<string>, CancellationToken, Task<JobOutcome>> work,
        CancellationToken cancellationToken);

    Task<Job?> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Streams the job's events: first a replay of progress lines already recorded, then live ones,
    /// then a terminal state event; the sequence completes after that. Unknown ids yield nothing.
    /// </summary>
    IAsyncEnumerable<JobEvent> SubscribeAsync(string id, CancellationToken cancellationToken);

    /// <summary>Requests cancellation. Returns false when the job is unknown or already finished.</summary>
    Task<bool> CancelAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Consumes the job's one-time secret (the VM-scoped token of a creation job). Returns it to the
    /// first caller only; every later call — and every call after a restart — gets <c>null</c>.
    /// </summary>
    Task<string?> TakeOneTimeSecretAsync(string id, CancellationToken cancellationToken);
}
