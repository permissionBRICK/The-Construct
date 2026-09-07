using System.Text.Json;
using Constructd.Api.Auth;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Endpoints;

public static class CascadeEndpoints
{
    // Caller owns the parent's VM gate. Each child's gate is held through atomic acceptance.
    public static async Task<IResult> DeleteAsync(Vm parent, HttpContext http, CancellationToken ct)
    {
        var s = http.RequestServices; var delegation = s.GetRequiredService<IVmDelegationRepository>();
        var clock = s.GetRequiredService<IClock>(); var gate = s.GetRequiredService<IVmOperationGate>();
        if (await LifecycleEndpoints.LiveAsync(parent, s, ct)) return Results.Ok(new { jobId = parent.CurrentJobId, replayed = true });
        string? token = null;
        if (http.Request.ContentLength > 0 || http.Request.Headers.TransferEncoding.Count > 0)
        {
            try
            {
                using var body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct);
                if (body.RootElement.TryGetProperty("cascade", out var cascade) && cascade.ValueKind == JsonValueKind.Object &&
                    cascade.TryGetProperty("token", out var value) && value.ValueKind == JsonValueKind.String) token = value.GetString();
                else return CodedProblems.Validation("cascade", "Expected the preview token.");
            }
            catch (JsonException) { return CodedProblems.Validation("cascade", "Invalid confirmation."); }
        }
        var children = await delegation.ListChildrenAsync(parent.Name, ct);
        async Task<CascadePreview> Preview()
        {
            var listed = new List<CascadeChild>();
            foreach (var vm in await delegation.ListChildrenAsync(parent.Name, ct))
                listed.Add(new(vm.Name, vm.Incarnation, vm.Sharing, vm.State, vm.DiskGb,
                    (await s.GetRequiredService<IMediaStore>().ListReferencesForVmAsync(vm.Name, ct)).Count));
            return await delegation.SaveCascadePreviewAsync(new(parent.Name, parent.Incarnation, Guid.NewGuid().ToString("n"), clock.UtcNow,
                clock.UtcNow.AddMinutes(10), listed, CascadeState.Previewed, null, new Dictionary<string,string>()), ct);
        }
        static IResult Confirmation(CascadePreview preview, string code) => LifecycleEndpoints.Problem(code, extra: new()
            { ["children"] = preview.Children, ["cascadeToken"] = preview.Token, ["expiresAt"] = preview.ExpiresAt });
        if (token is null && children.Count > 0) return Confirmation(await Preview(), "cascade-confirmation-required");
        var preview = token is null ? await Preview() : await delegation.GetCascadePreviewAsync(parent.Name, ct);
        if (preview is null || preview.Token != token && token is not null) return Confirmation(await Preview(), "cascade-scope-changed");
        if (preview.ExpiresAt <= clock.UtcNow) return LifecycleEndpoints.Problem("cascade-token-expired");
        var id = Guid.NewGuid().ToString("n");
        var job = new Job(id, children.Count > 0 ? "parent-cascade-delete" : "remove-vm", parent.Name, parent.Owner, JobState.Queued,
            [], null, null, clock.UtcNow, null, http.User.Actor());
        var childGates = new List<IAsyncDisposable>();
        IDisposable? maintenance = s.GetRequiredService<IMaintenanceGate>().TryEnter(job.Kind, id, parent.Name);
        if (maintenance is null) return LifecycleEndpoints.Problem("maintenance", 503);
        maintenance = new Handles(maintenance, s.GetRequiredService<IOperationRegistry>().Register(id, job.Kind, parent.Name));
        try
        {
            foreach (var child in children.OrderBy(v => v.Name, Ownership.NameComparer))
            {
                var held = await gate.TryAcquireAsync(child.Name, id, ct);
                if (held is null) { gate.IsHeld(child.Name, out var operation); return LifecycleEndpoints.Busy(operation); }
                childGates.Add(held);
            }
            var admission = s.GetRequiredService<IAdmissionStore>();
            var accepted = await admission.AdmitAsync(new(null, null, null, [], [], [], null, preview, job, parent.Name, id, true), ct);
            if (accepted.Outcome != AdmissionOutcome.Accepted)
            {
                if (accepted.Cascade?.Reason == "operation-in-progress") return LifecycleEndpoints.Busy(null);
                if (accepted.Cascade?.Reason == "cascade-token-expired") return LifecycleEndpoints.Problem("cascade-token-expired");
                return Confirmation(await Preview(), token is null ? "cascade-confirmation-required" : "cascade-scope-changed");
            }
            var sessions = s.GetRequiredService<IConsoleSessionStore>();
            sessions.RemoveForPrincipal("vm:" + parent.Name); sessions.RemoveForVm(parent.Name);
            foreach (var child in accepted.Cascade!.CurrentChildren) sessions.RemoveForVm(child.Name);
            var worker = s.GetRequiredService<CascadeJobs>();
            try
            {
                await s.GetRequiredService<IPersistedJobRunner>().StartPersistedAsync(job, maintenance,
                    (progress, token) => worker.RunAsync(job, parent, preview, progress, token), CancellationToken.None);
                maintenance = null;
            }
            catch
            {
                await admission.MarkStartFailedAsync(job.Id, "Cascade job could not start.", CancellationToken.None);
                return LifecycleEndpoints.Problem("job-start-failed", 500);
            }
            CodedProblems.Audit(http, "vm.delete", parent.Owner, parent.Name, parent.Name, "job=" + id + ", cascade=" + children.Count);
            return Results.Accepted("/api/v1/jobs/" + id, new { jobId = id });
        }
        finally { foreach (var held in childGates.AsEnumerable().Reverse()) await held.DisposeAsync(); maintenance?.Dispose(); }
    }
    private sealed class Handles(IDisposable first, IDisposable second) : IDisposable
    { public void Dispose() { try { second.Dispose(); } finally { first.Dispose(); } } }
}
