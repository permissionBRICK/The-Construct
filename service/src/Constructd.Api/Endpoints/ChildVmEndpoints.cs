using System.Text.Json;
using System.Text.RegularExpressions;
using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;
using Microsoft.AspNetCore.Authorization;
namespace Constructd.Api.Endpoints;

public static class ChildVmEndpoints
{
    public static RouteGroupBuilder MapChildVmEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/vms/{parent}/children", CreateAsync).RequireAuthorization(Policies.UserOrPrimaryToken).Audited("child.create").WithName("CreateChildVm");
        return api;
    }

    private static async Task<IResult> CreateAsync(string parent, JsonElement body, HttpContext http,
        IVmRepository vms, IAuthorizationService authorization, IDelegationPolicy policy, IChildVmDriver driver,
        IChildVmStorage storage, IHostConfigStore config, ConstructdOptions options, IMediaStore media,
        IMediaGate mediaGate, IVmOperationGate vmGate, IMaintenanceGate maintenance, IOperationKeyStore keys,
        IAdmissionStore admission, IPersistedJobRunner runner, ChildCreateJob worker, IClock clock, CancellationToken ct)
    {
        ChildCreateRequest request; ChildHardware hardware; long? seconds;
        try
        {
            request = body.Deserialize<ChildCreateRequest>(ApiJson.Options) ?? throw new ChildValidationException("validation", "body");
            if (request.Lifetime is null) return CodedProblems.Create(400, "lifetime-required", "An explicit lifetime is required.");
            seconds = ParseLifetime(request.Lifetime, clock.UtcNow);
            var fw = request.Firmware;
            hardware = HardwarePresets.Resolve(request.Cpus, request.RamMb, request.DiskGb, request.Preset, fw?.Generation, fw?.SecureBoot, fw?.SecureBootTemplate, fw?.Tpm, fw?.BootOrder, request.Media?.AuxiliaryMediaId is not null, request.Network?.Attach ?? true);
            if (request.Media is null || string.IsNullOrWhiteSpace(request.Media.InstallMediaId)) return CodedProblems.Validation("media", "Install media is required.");
        }
        catch (JsonException) { return CodedProblems.Validation("body", "Invalid child VM request."); }
        catch (ChildValidationException ex) { return Problem(ex); }
        var key = http.Request.Headers["X-Construct-Operation-Key"].FirstOrDefault() ?? request.OperationKey;
        if (key is not null && !OperationFingerprint.ValidKey(key)) return CodedProblems.Validation("operationKey", "Expected 8–128 operation-key characters.");
        var parentVm = await vms.GetAsync(parent, ct);
        if (parentVm is null) return Problems.NotFound("Unknown parent VM.");

        var relationship = await DelegationAuthorization.RelationshipAsync(http.User, parentVm, vms, http.RequestServices.GetRequiredService<IUserStore>(), ct);
        if (relationship is null or ForwardRelationship.Shared) return Problems.Forbidden("Parent delegation refused.");
        if (parentVm.Kind != VmKind.Primary) return CodedProblems.Create(409, "not-a-primary", "Children require a primary parent.");
        if (admission is UnsupportedFeaturePlatform || media is UnsupportedFeaturePlatform) return CodedProblems.Create(409, "unsupported-capability", "Child VM admission and media storage are unavailable.");
        var allowance = await policy.ResolveAsync(parentVm.Owner, parentVm.Name, ct);
        if (!allowance.AllowChildCreation) return CodedProblems.Create(403, "delegation-disabled", "Child creation is disabled.");
        if (parentVm.Deleting || parentVm.ChildCreationClosed) return CodedProblems.Create(409, "parent-closed", "Parent is closed to child creation.");
        if (!(await authorization.AuthorizeAsync(http.User, parentVm, Policies.ParentDelegate)).Succeeded) return Problems.Forbidden("Parent delegation refused.");
        if (seconds is null ? !allowance.AllowNeverLifetime : allowance.MaxChildLifetimeSeconds is long max && seconds > max)
            return CodedProblems.Create(403, "lifetime-not-allowed", "Requested lifetime exceeds the effective allowance.");
        var name = request.Name ?? OperationFingerprint.ChildName(parentVm.Name, parentVm.Owner, key);
        if (!VmNameValidator.IsValid(name)) return CodedProblems.Validation("name", VmNameValidator.Rule);
        http.SetAuditTarget(name);
        var fingerprint = OperationFingerprint.Compute("/vms/" + parentVm.Name + "/children", body);
        // Replays re-authorize above but do not depend on media still existing after the original job.
        if (key is not null && await keys.GetAsync(parentVm.Owner, "child-create", key, ct) is { } existing)
            return Replay(existing, fingerprint, name);
        var job = new Job(Guid.NewGuid().ToString("n"), "child-create", name, parentVm.Owner, JobState.Queued, [], null, null, clock.UtcNow, null, http.User.NameOrEmpty(), key);
        IDisposable? maintenanceHandle = maintenance.TryEnter(job.Kind, job.Id, name);
        if (maintenanceHandle is null) return CodedProblems.Create(503, "maintenance", "Host maintenance is draining mutations.");
        maintenanceHandle = new AdmissionHandle(maintenanceHandle, http.RequestServices.GetRequiredService<IOperationRegistry>().Register(job.Id, job.Kind, name));
        var mediaHandles = new List<IAsyncDisposable>();
        try
        {
            await using var parentGate = await vmGate.AcquireAsync(parentVm.Name, job.Id, ct);
            parentVm = (await vms.GetAsync(parentVm.Name, ct))!;
            if (parentVm is null || parentVm.Deleting || parentVm.ChildCreationClosed) return CodedProblems.Create(409, "parent-closed", "Parent is closed to child creation.");
            if (!(await authorization.AuthorizeAsync(http.User, parentVm, Policies.ParentDelegate)).Succeeded) return Problems.Forbidden("Parent delegation refused.");
            if (await LifecycleEndpoints.LiveAsync(parentVm, http.RequestServices, ct)) return LifecycleEndpoints.Busy(parentVm.CurrentJobId);
            allowance = await policy.ResolveAsync(parentVm.Owner, parentVm.Name, ct);
            if (seconds is null ? !allowance.AllowNeverLifetime : allowance.MaxChildLifetimeSeconds is long currentMax && seconds > currentMax)
                return CodedProblems.Create(403, "lifetime-not-allowed", "Requested lifetime exceeds the effective allowance.");
            foreach (var id in new[] { request.Media!.InstallMediaId, request.Media.AuxiliaryMediaId }.OfType<string>().Distinct().Order(StringComparer.Ordinal))
                mediaHandles.Add(await mediaGate.AcquireAsync(id, job.Id, ct));
            var references = new List<MediaReference>();
            foreach (var pair in new[] { (request.Media.InstallMediaId, MediaSlot.Install), (request.Media.AuxiliaryMediaId, MediaSlot.Auxiliary) })
            {
                if (pair.Item1 is null) continue;
                var item = await media.GetAsync(pair.Item1, ct);
                if (item is null) return Problems.NotFound("Unknown media.");
                if (!http.User.IsAdmin() && !Ownership.SameName(item.Owner, parentVm.Owner)) return Problems.Forbidden("Media belongs to another owner.");
                if (item.State != MediaState.Ready) return CodedProblems.Create(409, "media-not-ready", "Media is not ready.");
                if (item.DedicatedTo is not null && !Ownership.SameName(item.DedicatedTo, name)) return Problems.Forbidden("Media is dedicated to another VM.");
                if (item.Role != (pair.Item2 == MediaSlot.Install ? MediaRole.Install : MediaRole.Auxiliary)) return CodedProblems.Validation("media", "Media role does not match its slot.");
                references.Add(new(item.Id, name, pair.Item2, clock.UtcNow));
            }
            try { HardwarePresets.ValidateCapabilities(hardware, await driver.GetCapabilitiesAsync(ct), request.Media.AuxiliaryMediaId is not null); }
            catch (ChildValidationException ex) { return Problem(ex); }
            var capacityConfig = await HostAdminEndpoints.CapacityConfigAsync(config, options, ct);
            if (capacityConfig.MaxVcpusPerVm is int maxCpu && request.Cpus > maxCpu) return CodedProblems.Validation("cpus", "CPU count exceeds the host per-VM limit.");
            if (await driver.GetVmIdAsync(name, ct) is not null) return CodedProblems.Create(409, "name-taken", "The hypervisor name is already in use.");
            var placement = await storage.ResolveStorageAsync(name, ct);
            var lease = new Lease(request.Lifetime!, seconds, null, null, LeaseState.Inactive, 0, null, null);
            var vm = new Vm(name, parentVm.Owner, hardware.Cpus, 0, hardware.DiskGb, clock.UtcNow, VmState.Unknown, null, null, IdlePolicy.Disabled, [], Kind: VmKind.Child, Parent: parentVm.Name, RamMb: hardware.RamMb, Lease: lease, Hardware: hardware, CurrentJobId: job.Id);
            var lines = new List<ReservationLine> { new(ReservationResource.Storage, (long)hardware.DiskGb << 30, "disk:" + placement.DiskPath, placement.DiskVolume), new(ReservationResource.Storage, vm.RamBytes + (64L << 20), "saved-state:" + name, placement.ConfigVolume) };
            if (request.Start) { lines.Add(new(ReservationResource.Ram, vm.RamBytes, null, null)); lines.Add(new(ReservationResource.Cpu, hardware.Cpus, null, null)); }
            OperationKeyRecord? operation = key is null ? null : new(parentVm.Owner, job.Kind, key, fingerprint, name, job.Id, OperationKeyState.Completed, null, null, JsonSerializer.Serialize(new { jobId = job.Id }, ApiJson.Options), clock.UtcNow);
            var ownerLimit = (await policy.ResolveAsync(parentVm.Owner, null, ct)).MaxRetainedChildren;
            var result = await admission.AdmitAsync(new(operation, vm, allowance, [], [], references, new(parentVm.Owner, name, job.Id, lines, TimeSpan.FromHours(2)), null, job, null, null, false, OwnerChildrenLimit: ownerLimit), ct);
            if (result.Outcome == AdmissionOutcome.Replay) return Replay(result.ExistingKey!, fingerprint, name);
            if (result.Outcome != AdmissionOutcome.Accepted) return AdmissionProblem(result);
            try
            {
                await runner.StartPersistedAsync(job, maintenanceHandle, (progress, token) => worker.RunAsync(job, vm, placement, request.Start, result.ReservationIds, progress, token), CancellationToken.None);
                maintenanceHandle = null;
            }
            catch
            {
                await admission.MarkStartFailedAsync(job.Id, "Child job could not be started; recovery retains its admitted artifacts.", CancellationToken.None);
                return CodedProblems.Create(500, "job-start-failed", "Child job could not be started.");
            }
            CodedProblems.Audit(http, "child.create", vm.Owner, vm.Parent, vm.Name, "job=" + job.Id);
            return Results.Accepted("/api/v1/jobs/" + job.Id, new { jobId = job.Id });
        }
        finally
        {
            for (var i = mediaHandles.Count - 1; i >= 0; i--) await mediaHandles[i].DisposeAsync();
            maintenanceHandle?.Dispose();
        }
    }

    // Called by the existing DELETE dispatcher with its VM gate already held.
    public static async Task<IResult> DeleteAsync(Vm vm, HttpContext http, CancellationToken ct)
    {
        var services = http.RequestServices;
        var clock = services.GetRequiredService<IClock>();
        var admission = services.GetRequiredService<IAdmissionStore>();
        if (admission is UnsupportedFeaturePlatform || services.GetRequiredService<IMediaStore>() is UnsupportedFeaturePlatform) return CodedProblems.Create(409, "unsupported-capability", "Child VM admission and media storage are unavailable.");
        if (await LifecycleEndpoints.LiveAsync(vm, services, ct)) return Results.Ok(new { jobId = vm.CurrentJobId, replayed = true });
        var runner = services.GetRequiredService<IPersistedJobRunner>();
        var worker = services.GetRequiredService<ChildDeleteJob>();
        var key = http.Request.Headers["X-Construct-Operation-Key"].FirstOrDefault();
        if (key is not null && !OperationFingerprint.ValidKey(key)) return CodedProblems.Validation("operationKey", "Invalid operation key.");
        using var body = JsonDocument.Parse("{}");
        var fingerprint = OperationFingerprint.Compute("DELETE /vms/" + vm.Name, body.RootElement);
        if (key is not null && await services.GetRequiredService<IOperationKeyStore>().GetAsync(vm.Owner, "child-delete", key, ct) is { } existing)
            return Replay(existing, fingerprint, vm.Name);
        var job = new Job(Guid.NewGuid().ToString("n"), "child-delete", vm.Name, vm.Owner, JobState.Queued, [], null, null, clock.UtcNow, null, http.User.NameOrEmpty(), key);
        IDisposable? handle = services.GetRequiredService<IMaintenanceGate>().TryEnter(job.Kind, job.Id, vm.Name);
        if (handle is null) return CodedProblems.Create(503, "maintenance", "Host maintenance is draining mutations.");
        handle = new AdmissionHandle(handle, services.GetRequiredService<IOperationRegistry>().Register(job.Id, job.Kind, vm.Name));
        try
        {
            OperationKeyRecord? operation = key is null ? null : new(vm.Owner, job.Kind, key, fingerprint, vm.Name, job.Id, OperationKeyState.Completed, null, null, JsonSerializer.Serialize(new { jobId = job.Id }, ApiJson.Options), clock.UtcNow);
            var accepted = await admission.AdmitAsync(new(operation, null, null, [], [], [], null, null, job, vm.Name, job.Id, false), ct);
            if (accepted.Outcome == AdmissionOutcome.Replay) return Replay(accepted.ExistingKey!, fingerprint, vm.Name);
            if (accepted.Outcome != AdmissionOutcome.Accepted) return AdmissionProblem(accepted);
            services.GetRequiredService<IConsoleSessionStore>().RemoveForVm(vm.Name);
            try
            {
                await runner.StartPersistedAsync(job, handle, (progress, token) => worker.RunAsync(job, vm, progress, token), CancellationToken.None);
                handle = null;
            }
            catch
            {
                await admission.MarkStartFailedAsync(job.Id, "Child deletion could not start; its fence remains for recovery.", CancellationToken.None);
                return CodedProblems.Create(500, "job-start-failed", "Child deletion could not start.");
            }
            CodedProblems.Audit(http, "child.delete", vm.Owner, vm.Parent, vm.Name, "job=" + job.Id);
            return Results.Accepted("/api/v1/jobs/" + job.Id, new { jobId = job.Id });
        }
        finally { handle?.Dispose(); }
    }

    private sealed class AdmissionHandle(IDisposable maintenance, IDisposable operation) : IDisposable
    {
        public void Dispose() { try { operation.Dispose(); } finally { maintenance.Dispose(); } }
    }

    internal static long? ParseLifetime(string input, DateTimeOffset now) => LifetimeParser.Parse(input, now);
    private static IResult Replay(OperationKeyRecord existing, string fingerprint, string target) => existing.Fingerprint == fingerprint && Ownership.SameName(existing.Target, target)
        ? Results.Ok(new { jobId = existing.JobId, replayed = true }) : CodedProblems.Create(409, "operation-key-conflict", "Operation key was already used for another request.");
    private static IResult Problem(ChildValidationException ex) => CodedProblems.Create(ex.Code == "validation" ? 400 : 409, ex.Code, ex.Message, ex.Field);
    private static IResult AdmissionProblem(AdmissionResult result) => result.Capacity is { Allowed: false } decision ? PrimaryVmAdmission.CapacityProblem(decision) : CodedProblems.Create(409, result.Outcome switch
    {
        AdmissionOutcome.VersionConflict => "operation-in-progress", AdmissionOutcome.NameTaken => "name-taken", AdmissionOutcome.KeyConflict => "operation-key-conflict", AdmissionOutcome.ParentClosed => "parent-closed",
        AdmissionOutcome.ParentMissing => "not-a-primary", AdmissionOutcome.MediaNotReady => "media-not-ready",
        AdmissionOutcome.CapacityRefused => result.Capacity?.Reason == "inventory-incomplete" ? "capacity-unavailable" : "capacity-exhausted",
        AdmissionOutcome.QuotaExceeded => "capacity-exhausted", _ => "operation-key-conflict"
    }, "Child admission was refused.");
}
