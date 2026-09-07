using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

public interface IPersistedJobRunner
{
    /// <summary>Runs a job whose Queued row was written by an AdmissionPlan. <paramref name="gateHandle"/> was taken BEFORE the plan committed and is owned by the runner until the job is terminal.</summary>
    Task StartPersistedAsync(Job queued, IDisposable gateHandle, Func<IProgress<string>, CancellationToken, Task<JobOutcome>> work, CancellationToken ct);
    Task SetPhaseAsync(string jobId, string phase, CancellationToken ct);
}
