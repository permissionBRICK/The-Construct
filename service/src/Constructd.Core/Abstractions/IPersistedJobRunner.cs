using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

public interface IPersistedJobRunner
{
    /// <summary>Runs a job whose Queued row was written by an AdmissionPlan. <paramref name="gateHandle"/> was taken BEFORE the plan committed and is owned by the runner until the job is terminal.</summary>
    Task StartPersistedAsync(Job queued, IDisposable gateHandle, Func<IProgress<string>, CancellationToken, Task<JobOutcome>> work, CancellationToken ct);
    Task SetPhaseAsync(string jobId, string phase, CancellationToken ct);
}

/// <summary>A failed job may expose a service-composed, secret-free cleanup result.</summary>
public sealed class JobFailureException(string message, object result) : Exception(message), Constructd.Core.Logic.IConstructdError
{
    public object Result { get; } = result;
}
