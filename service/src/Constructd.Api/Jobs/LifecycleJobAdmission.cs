using System.Text.Json;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Api.Jobs;

public sealed class LifecycleJobAdmission(IAdmissionStore admission, IPersistedJobRunner runner,
    IMaintenanceGate maintenance, IOperationRegistry operations, ChildLifecycleJobs worker, IClock clock)
{
    public async Task<Job> SubmitAsync(Vm vm, string initiator, bool restart, long? leaseVersion, OperationKeyRecord? key, CancellationToken ct)
    {
        var job = new Job(Guid.NewGuid().ToString("n"), restart ? "vm-restart" : "vm-shutdown", vm.Name, vm.Owner,
            JobState.Queued, [], null, null, clock.UtcNow, null, leaseVersion is null ? initiator : null, key?.Key);
        IDisposable? handle = maintenance.TryEnter("mutation:" + job.Kind, job.Id, vm.Name);
        if (handle is null) throw new LifecycleException("maintenance");
        handle = new Handles(handle, operations.Register(job.Id, job.Kind, vm.Name));
        try
        {
            if (key is not null) key = key with { JobId = job.Id, State = OperationKeyState.Completed, ResponseJson = JsonSerializer.Serialize(new { jobId = job.Id }, ApiJson.Options) };
            var accepted = await admission.AdmitAsync(new(key, null, null, [], [], [], null, null, job, null, null, false, VmToAssignJob: vm.Name), ct);
            if (accepted.Outcome != AdmissionOutcome.Accepted) throw new LifecycleException("operation-in-progress");
            try
            {
                await runner.StartPersistedAsync(job, handle, (progress, token) => worker.RunAsync(job, restart, leaseVersion, progress, token), CancellationToken.None);
                handle = null;
            }
            catch
            {
                await admission.MarkStartFailedAsync(job.Id, "Lifecycle job could not start.", CancellationToken.None);
                throw new LifecycleException("job-start-failed");
            }
            return job;
        }
        finally { handle?.Dispose(); }
    }
    private sealed class Handles(IDisposable first, IDisposable second) : IDisposable
    { public void Dispose() { try { second.Dispose(); } finally { first.Dispose(); } } }
}
