namespace Constructd.Core.Domain;

/// <summary>
/// A timestamped progress line of a job, as streamed over SSE. A job that reports steps (design 7.1)
/// also fills <paramref name="Step"/>, <paramref name="State"/> and, where known, <paramref name="Percent"/>;
/// <paramref name="Text"/> is then the output line, or a short "step: state" summary.
/// </summary>
public sealed record JobProgressLine(DateTimeOffset At, string Text, string? Step = null, string? State = null, int? Percent = null);

/// <summary>Structured progress of a step-wise job: <c>{step, state, line?, percent?}</c>.</summary>
public sealed record JobStepProgress(string Step, string State, string? Line = null, int? Percent = null);

/// <summary>
/// Implemented by the engine's progress sink: a job that reports steps checks for it and falls back
/// to plain lines otherwise.
/// </summary>
public interface IJobStepProgress : IProgress<string>
{
    void Report(JobStepProgress value);
}

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
    DateTimeOffset? Finished,
    string? Initiator = null,
    string? OperationKey = null,
    string? Phase = null);

/// <summary>Well-known <see cref="Job.Kind"/> values.</summary>
public static class JobKinds
{
    public const string CreateVm = "create-vm";
    public const string RemoveVm = "remove-vm";
    public const string Provision = "provision";
    public const string Reinstall = "reinstall";
    public const string Restore = "restore";
}

/// <summary>
/// Result of a <see cref="JobKinds.CreateVm"/> job. It is durable and carries no secret; the
/// VM-scoped token is handed out exactly once through the job's one-time secret channel.
/// </summary>
/// <param name="Provision">The first provision, when the create request carried settings; null (and left out) otherwise.</param>
public sealed record VmCreateResult(string Name, Endpoint Endpoint,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ProvisionJobResult? Provision = null);

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
    Phase,
}

/// <summary>
/// One SSE event: either a progress line or the job's (final) state. Subscribers first receive a
/// replay of the progress lines already recorded, then live events, then a terminal
/// <see cref="JobEventKind.State"/> event, after which the stream completes.
/// </summary>
public sealed record JobEvent(JobEventKind Kind, JobProgressLine? Progress, Job? Job);
