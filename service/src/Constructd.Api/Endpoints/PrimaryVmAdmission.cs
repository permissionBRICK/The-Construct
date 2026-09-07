using System.Text.Json;
using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Endpoints;

/// <summary>Admission around the existing primary provisioning workflow; the workflow is unchanged.</summary>
public static class PrimaryVmAdmission
{
    public static async Task<IResult> CreateAsync(Vm vm, VmDescriptor descriptor, CreateVmRequest request, HttpContext http, CancellationToken ct)
    {
        var services = http.RequestServices;
        var clock = services.GetRequiredService<IClock>();
        var supplied = http.Request.Headers["X-Construct-Operation-Key"].FirstOrDefault();
        if (supplied is not null && !OperationFingerprint.ValidKey(supplied)) return CodedProblems.Validation("operationKey", "Invalid operation key.");
        var id = Guid.NewGuid().ToString("n");
        var key = supplied is null ? null : new OperationKeyRecord(vm.Owner, "create-vm", supplied,
            OperationFingerprint.Compute(http.Request.Path, JsonSerializer.SerializeToElement(request, ApiJson.Options)), vm.Name, id,
            OperationKeyState.Completed, null, null, JsonSerializer.Serialize(new { jobId = id }, ApiJson.Options), clock.UtcNow);
        var prior = key is null ? null : await services.GetRequiredService<IOperationKeyStore>().GetAsync(vm.Owner, key.Kind, key.Key, ct);
        if (prior is not null) return prior.Fingerprint == key!.Fingerprint && Ownership.SameName(prior.Target, vm.Name)
            ? LifecycleEndpoints.Replay(prior) : LifecycleEndpoints.Problem("operation-key-conflict");
        await using var gate = await PrimaryOperationGate.AcquireAsync(services.GetRequiredService<IVmOperationGate>(), vm.Name, id, ct);
        if (gate is null) return LifecycleEndpoints.Busy(null);
        var observed = await services.GetRequiredService<IHypervisorDriver>().GetStateAsync(vm.Name, ct);
        if (observed != VmState.Absent)
        {
            http.SetAuditDetail(observed == VmState.Unknown ? "state unknown" : "name already taken");
            return observed == VmState.Unknown ? LifecycleEndpoints.Problem("vm-state-unknown") :
                Problems.Conflict($"A VM named '{vm.Name}' already exists on this host.");
        }
        ChildStoragePlacement? placement = null;
        try { placement = await services.GetRequiredService<IChildVmStorage>().ResolvePrimaryStorageAsync(vm.Name, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* Unknown placement is evidence for the ledger, never a new primary-driver dependency. */ }
        var lines = new List<ReservationLine> { new(ReservationResource.Ram, vm.RamBytes, null, null),
            new(ReservationResource.Cpu, vm.Cpu, null, null), new(ReservationResource.Storage, vm.DiskGb * 1073741824L, placement is null ? "unresolved-primary-disk:" + vm.Name : "disk:" + placement.DiskPath, placement?.DiskVolume ?? "unknown") };
        if (services.GetRequiredService<IHypervisorDriver>().Capabilities.Suspend)
            lines.Add(new(ReservationResource.Storage, vm.RamBytes + CapacityMath.SavedStateOverhead, "saved-state:" + vm.Name, placement?.ConfigVolume ?? "unknown"));
        var job = new Job(id, "create-vm", vm.Name, vm.Owner, JobState.Queued, [], null, null, clock.UtcNow, null, http.User.Actor(), supplied);
        var allowance = await services.GetRequiredService<IDelegationPolicy>().ResolveAsync(vm.Owner, null, ct);
        var admission = services.GetRequiredService<IAdmissionStore>();
        IDisposable? handle = services.GetRequiredService<IMaintenanceGate>().TryEnter(job.Kind, id, vm.Name);
        if (handle is null) return await MaintenanceFilter.RefusedAsync(http);
        handle = new Handles(handle, services.GetRequiredService<IOperationRegistry>().Register(id, job.Kind, vm.Name));
        try
        {
            var result = await admission.AdmitAsync(new(key, vm with { CurrentJobId = id }, allowance, [], [], [],
                new(vm.Owner, vm.Name, id, lines, TimeSpan.FromMinutes(10)), null, job, null, null, false), ct);
            if (result.Outcome == AdmissionOutcome.Replay) return LifecycleEndpoints.Replay(result.ExistingKey!);
            if (result.Outcome == AdmissionOutcome.NameTaken)
            {
                http.SetAuditDetail("name already taken");
                return Problems.Conflict($"A VM named '{vm.Name}' already exists on this host.");
            }
            if (result.Outcome == AdmissionOutcome.QuotaExceeded)
            {
                var owned = await services.GetRequiredService<IVmDelegationRepository>().CountByOwnerAsync(vm.Owner, VmKind.Primary, ct);
                http.SetAuditDetail($"quota {owned}/{allowance.MaxPrimaries}");
                return Problems.Forbidden($"Quota reached: you own {owned} of {allowance.MaxPrimaries} allowed VMs.");
            }
            if (result.Capacity is { Allowed: false } decision) return CapacityProblem(decision);
            if (result.Outcome != AdmissionOutcome.Accepted) return LifecycleEndpoints.Problem("operation-key-conflict");
            var worker = services.GetRequiredService<Constructd.Api.Jobs.PrimaryVmJobs>();
            try
            {
                await services.GetRequiredService<IPersistedJobRunner>().StartPersistedAsync(job, handle,
                    (progress, token) => worker.CreateAsync(job, vm, descriptor, result.ReservationIds, request.Opts?.Redownload == true, progress, token), CancellationToken.None);
                handle = null;
            }
            catch { await admission.MarkStartFailedAsync(id, "Primary job could not start.", CancellationToken.None); return LifecycleEndpoints.Problem("job-start-failed", 500); }
            http.SetAuditDetail($"job={id}, cpu={vm.Cpu}, ramGb={vm.RamGb}, diskGb={vm.DiskGb}");
            return Results.Accepted("/api/v1/jobs/" + id, new JobAcceptedResponse(id));
        }
        finally { handle?.Dispose(); }
    }
    public static IResult CapacityProblem(CapacityDecision d) => LifecycleEndpoints.Problem(d.Reason == "reservation-conflict" ? "operation-in-progress" : d.Reason == "inventory-incomplete" ? "capacity-unavailable" : "capacity-exhausted", extra: new()
        { ["resource"] = d.Resource, ["scope"] = d.Scope, ["requested"] = d.Requested, ["allowed"] = d.AllowedAmount, ["available"] = d.Available, ["reason"] = d.Reason, ["epoch"] = d.Epoch });
    private sealed class Handles(IDisposable first, IDisposable second) : IDisposable
    { public void Dispose() { try { second.Dispose(); } finally { first.Dispose(); } } }
}
