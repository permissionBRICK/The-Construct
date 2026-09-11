using System.Text.Json;
using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Api.Source;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Microsoft.AspNetCore.Authorization;
namespace Constructd.Api.Endpoints;

public static class SourceEndpoints
{
    public static RouteGroupBuilder MapSourceEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/vms/{name}/source", EnsureAsync).RequireAuthorization(Policies.User).Audited("vm.source.ensure", "name");
        api.MapGet("/vms/{name}/source/{commit}", FetchAsync).RequireAuthorization(Policies.VmScoped);
        api.MapGet("/host/source-cache", ListAsync).RequireAuthorization(Policies.Admin);
        api.MapPost("/host/source-cache/cleanup", CleanupAsync).RequireAuthorization(Policies.Admin).Audited("host.source.cleanup");
        api.MapDelete("/host/source-cache/{commit}", DeleteAsync).RequireAuthorization(Policies.Admin).Audited("host.source.delete", "commit");
        return api;
    }
    private static IResult Error(string code, Dictionary<string, object?>? extra = null) => CodedProblems.Create(
        code == "source-not-cached" ? 404 : code == "validation" ? 400 : 409, code, code, extra: extra);
    private static IResult? Check(Vm vm, ConstructdOptions options) => !options.HostAdmin.Source.Enabled ? Error("unsupported-capability") :
        vm.Deleting ? Error("vm-deleting") : vm.Kind != VmKind.Primary ? Error("not-a-primary") : null;
    private static SourceReadyResponse Ready(SourceItem item) => new(item.Commit, "ready", item.SizeBytes, item.Sha256, item.ReleaseTag);
    private static async Task<IResult> EnsureAsync(string name, SourceEnsureRequest request, HttpContext http,
        IVmRepository vms, IAuthorizationService authorization, ConstructdOptions options, ISourceCache cache,
        IAdmissionStore admission, IOperationKeyStore keys, IMaintenanceGate maintenance, IOperationRegistry operations,
        IPersistedJobRunner runner, IClock clock, CancellationToken ct)
    {
        var commit = request.Commit?.ToLowerInvariant();
        if (!SourceZipRules.ValidCommit(commit)) return CodedProblems.Validation("commit", "Expected a full 40-hex commit.");
        var lookup = await ApiHelpers.ResolveVmAsync(http, vms, authorization, name, Policies.VmOwnerOrAdmin, ct);
        if (!lookup.Ok) return lookup.Failure!;
        var vm = lookup.Vm!; if (Check(vm, options) is { } refused) return refused;
        var supplied = http.Request.Headers["X-Construct-Operation-Key"].FirstOrDefault();
        if (supplied is not null && !OperationFingerprint.ValidKey(supplied)) return CodedProblems.Validation("operationKey", "Invalid operation key.");
        var fingerprint = OperationFingerprint.Compute(http.Request.Path, JsonSerializer.SerializeToElement(new { commit }, ApiJson.Options));
        var prior = supplied is null ? null : await keys.GetAsync(vm.Owner, "source-ensure", supplied, ct);
        if (prior is not null) return prior.Fingerprint == fingerprint && Ownership.SameName(prior.Target, vm.Name) ? LifecycleEndpoints.Replay(prior) : Error("operation-key-conflict");
        var id = Guid.NewGuid().ToString("n");
        IDisposable? handle = maintenance.TryEnter("source-fetch", id, vm.Name);
        if (handle is null) return await MaintenanceFilter.RefusedAsync(http);
        handle = new Handles(handle, operations.Register(id, "source-fetch", vm.Name));
        try
        {
            var ready = await cache.ReadyAsync(commit!, ct);
            object response = ready is null ? new SourceQueuedResponse(id, commit!, "downloading") : Ready(ready);
            var job = ready is null ? new Job(id, "source-fetch", vm.Name, vm.Owner, JobState.Queued, [], null, null, clock.UtcNow, null, http.User.Actor(), supplied) : null;
            var key = supplied is null ? null : new OperationKeyRecord(vm.Owner, "source-ensure", supplied, fingerprint, vm.Name, job?.Id,
                OperationKeyState.Completed, null, null, JsonSerializer.Serialize(response, ApiJson.Options), clock.UtcNow);
            // ReadyAsync released the catalog lock; this admission is database-only in both paths.
            var result = await admission.AdmitAsync(new(key, null, null, [], [], [], null, null, job, null, null, false,
                VmSourceCommit: (vm.Name, commit!)), ct);
            if (result.Outcome == AdmissionOutcome.Replay) return LifecycleEndpoints.Replay(result.ExistingKey!);
            if (result.Outcome != AdmissionOutcome.Accepted) return Error(result.Outcome == AdmissionOutcome.VersionConflict ? "vm-deleting" : "operation-key-conflict");
            if (job is not null)
            {
                try
                {
                    await runner.StartPersistedAsync(job, handle, async (progress, token) =>
                    { var item = await cache.EnsureAsync(commit!, progress, token, id); return new JobOutcome(new { item.Commit, item.SizeBytes, item.Sha256, item.ReleaseTag }); }, CancellationToken.None);
                    handle = null;
                }
                catch { await admission.MarkStartFailedAsync(id, "job-start-failed", CancellationToken.None); return Error("job-start-failed"); }
            }
            CodedProblems.Audit(http, "vm.source.ensure", vm.Owner, vm.Parent, vm.Name, $"commit={commit}, state={(ready is null ? "downloading" : "ready")}, job={job?.Id}");
            return ready is null ? Results.Accepted("/api/v1/jobs/" + id, response) : Results.Ok(response);
        }
        finally { handle?.Dispose(); }
    }
    private static async Task FetchAsync(string name, string commit, HttpContext http, IVmRepository vms,
        IAuthorizationService authorization, ConstructdOptions options, ISourceCache cache, ISourceStore store, IAuditLog audit, IClock clock)
    {
        var ct = http.RequestAborted; commit = commit.ToLowerInvariant(); var outcome = "failure"; string? code = null; long bytes = 0;
        try
        {
            IResult? failure;
            if (!SourceZipRules.ValidCommit(commit)) { code = "validation"; failure = CodedProblems.Validation("commit", "Expected a full 40-hex commit."); }
            else
            {
                var lookup = await ApiHelpers.ResolveVmAsync(http, vms, authorization, name, Policies.VmSelfOrOwnerOrAdmin, ct);
                failure = lookup.Ok ? Check(lookup.Vm!, options) : lookup.Failure;
            }
            if (failure is not null)
            { await failure.ExecuteAsync(http); outcome = http.Response.StatusCode == 403 ? "denied" : "failure"; return; }
            SourceOpen opened;
            try { opened = await cache.OpenAsync(commit, ct); }
            catch (SourceException ex)
            {
                code = ex.Code; if (code == "source-downloading") http.Response.Headers.RetryAfter = "5";
                var item = await store.GetAsync(commit, ct);
                await Error(code, item?.Error is null ? null : new() { ["error"] = item.Error }).ExecuteAsync(http); return;
            }
            await using var stream = opened.Stream;
            http.Response.ContentType = "application/zip"; http.Response.ContentLength = opened.Item.SizeBytes;
            http.Response.Headers.ETag = "\"" + opened.Item.Sha256 + "\"";
            http.Response.Headers["X-Construct-Source-Commit"] = commit;
            http.Response.Headers["X-Construct-Source-Sha256"] = opened.Item.Sha256;
            http.Response.Headers.ContentDisposition = $"attachment; filename=construct-source-{commit}.zip";
            await stream.CopyToAsync(http.Response.Body, ct);
            bytes = opened.Item.SizeBytes; outcome = "success";
        }
        finally
        {
            // Never include unvalidated route text, credentials or exception messages.
            await ApiHelpers.AuditAsync(audit, clock, http.User, "vm.source.fetch", VmNameValidator.IsValid(name) ? name : "invalid",
                outcome == "success" ? AuditOutcome.Success : AuditOutcome.Failure,
                $"commit={(SourceZipRules.ValidCommit(commit) ? commit : "invalid")}, bytes={bytes}, outcome={outcome}, code={code}", CancellationToken.None);
        }
    }
    private static async Task<IResult> ListAsync(ISourceStore store, SourceCatalog catalog, IVmRepository vms, ConstructdOptions options, CancellationToken ct)
    {
        var limits = options.HostAdmin.Source;
        if (!limits.Enabled) return Error("unsupported-capability");
        using (await catalog.AcquireAsync(ct))
        {
            var machines = await vms.ListAsync(null, ct); var items = await store.ListAsync(ct);
            return Results.Ok(new { items = items.Select(i => new { i.Commit, i.State, i.SizeBytes, i.Sha256, i.ReleaseTag, i.Error, i.Created, i.ReadyAt, i.LastUsedAt,
                pinnedBy = machines.Where(v => v.SourceCommit is { } source && SourceZipRules.Pinned(i.Commit, source) || v.Guest?.ConstructCommit is { } guest && SourceZipRules.Pinned(i.Commit, guest)).Select(v => v.Name).ToArray(),
                readers = catalog.Readers.GetValueOrDefault(i.Commit) }).ToArray(), committedBytes = await store.CommittedBytesAsync(ct), limits.MaxTotalBytes, limits.MaxItemBytes, limits.Enabled });
        }
    }
    private static Task<IResult> CleanupAsync(HttpContext http, ISourceCache cache, IMaintenanceGate maintenance, IJobEngine jobs, ConstructdOptions options, CancellationToken ct) =>
        StartCleanupAsync(http, cache, maintenance, jobs, options, null, false, ct);
    private static Task<IResult> DeleteAsync(string commit, bool? force, HttpContext http, ISourceCache cache, IMaintenanceGate maintenance, IJobEngine jobs, ConstructdOptions options, CancellationToken ct) =>
        StartCleanupAsync(http, cache, maintenance, jobs, options, commit.ToLowerInvariant(), force == true, ct);
    private static async Task<IResult> StartCleanupAsync(HttpContext http, ISourceCache cache, IMaintenanceGate maintenance, IJobEngine jobs,
        ConstructdOptions options, string? commit, bool force, CancellationToken ct)
    {
        if (!options.HostAdmin.Source.Enabled) return Error("unsupported-capability");
        var activity = maintenance.TryEnter("source-cleanup", http.TraceIdentifier, null);
        if (activity is null) return await MaintenanceFilter.RefusedAsync(http);
        try
        {
            if (commit is not null)
            {
                CodedProblems.Audit(http, "host.source.delete", http.User.NameOrEmpty(), target: commit,
                    extra: "force=" + force.ToString().ToLowerInvariant());
                await cache.RequestDeleteAsync(commit, force, ct);
            }
            var initiator = http.User.Actor();
            var job = await jobs.SubmitAsync("source-cleanup", null, http.User.NameOrEmpty(), async (progress, token) =>
            { using (activity) return new JobOutcome(await cache.PruneAsync(initiator, progress, token, commit)); }, ct);
            CodedProblems.Audit(http, commit is null ? "host.source.cleanup" : "host.source.delete", http.User.NameOrEmpty(), target: commit,
                extra: $"force={force.ToString().ToLowerInvariant()}, outcome=accepted, job={job.Id}");
            return Results.Accepted("/api/v1/jobs/" + job.Id, new { jobId = job.Id });
        }
        catch (SourceException ex) { activity.Dispose(); return Error(ex.Code); }
        catch { activity.Dispose(); throw; }
    }
    private sealed class Handles(IDisposable first, IDisposable second) : IDisposable
    { public void Dispose() { try { second.Dispose(); } finally { first.Dispose(); } } }
}
