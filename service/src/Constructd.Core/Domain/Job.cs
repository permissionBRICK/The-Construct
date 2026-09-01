namespace Constructd.Core.Domain;

/// <summary>A timestamped progress line of a job, as streamed over SSE.</summary>
public sealed record JobProgressLine(DateTimeOffset At, string Text);

/// <summary>
/// A long-running operation. Mutating VM lifecycle calls answer <c>202 {jobId}</c> and the client
/// follows the job (plan §4.4).
/// </summary>
/// <param name="Kind">One of <see cref="JobKinds"/>.</param>
/// <param name="Owner">
/// Name of the principal that submitted the job. Not in the original plan sketch, but required:
/// job results carry secrets (the VM token) and a job outlives the VM record it deletes, so job
/// authorization cannot be derived from <see cref="VmName"/>.
/// </param>
/// <param name="Result">
/// Job-specific result payload (e.g. <see cref="VmCreateResult"/>), serialized by its runtime type.
/// Never contains a secret: secrets travel through the one-time channel
/// (<see cref="Abstractions.IJobEngine.TakeOneTimeSecretAsync"/>) and are never stored.
/// </param>
public sealed record Job(
    string Id,
    string Kind,
    string? VmName,
    string Owner,
    JobState State,
    IReadOnlyList<JobProgressLine> Progress,
    object? Result,
    string? Error,
    DateTimeOffset Created,
    DateTimeOffset? Finished);

/// <summary>Well-known <see cref="Job.Kind"/> values.</summary>
public static class JobKinds
{
    public const string CreateVm = "create-vm";
    public const string RemoveVm = "remove-vm";
}

/// <summary>
/// Result of a <see cref="JobKinds.CreateVm"/> job. It is durable and carries no secret; the
/// VM-scoped token is handed out exactly once through the job's one-time secret channel.
/// </summary>
public sealed record VmCreateResult(string Name, Endpoint Endpoint);

/// <summary>
/// What a job body returns: a durable, secret-free <paramref name="Result"/>, plus at most one
/// <paramref name="OneTimeSecret"/> that the engine keeps in memory and hands to the first caller
/// that asks for it — never written to a store, never replayed.
/// </summary>
public sealed record JobOutcome(object? Result, string? OneTimeSecret = null);

/// <summary>Result of a <see cref="JobKinds.RemoveVm"/> job.</summary>
public sealed record VmRemoveResult(string Name, int ReleasedForwards);

/// <summary>Kind of event pushed to an SSE subscriber.</summary>
public enum JobEventKind
{
    Progress,
    State,
}

/// <summary>
/// One SSE event: either a progress line or the job's (final) state. Subscribers first receive a
/// replay of the progress lines already recorded, then live events, then a terminal
/// <see cref="JobEventKind.State"/> event, after which the stream completes.
/// </summary>
public sealed record JobEvent(JobEventKind Kind, JobProgressLine? Progress, Job? Job);
