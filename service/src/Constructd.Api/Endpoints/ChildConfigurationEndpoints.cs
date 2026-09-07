using System.Text.Json;
using Constructd.Api.Auth;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Intent = Constructd.Api.Jobs.ConfigurationIntent;
namespace Constructd.Api.Endpoints;

public static class ChildConfigurationEndpoints
{
    private sealed record Firmware(bool? SecureBoot, SecureBootTemplate? SecureBootTemplate, bool? Tpm, IReadOnlyList<BootDevice>? BootOrder);
    private sealed record HardwareRequest(int? Cpus, int? RamMb, int? DiskGb, Firmware? Firmware);
    public static RouteGroupBuilder MapChildConfigurationEndpoints(this RouteGroupBuilder api)
    {
        api.MapPut("/vms/{name}/hardware", (string name, JsonElement body, HttpContext http, CancellationToken ct) => ChangeAsync(name, body, http, false, ct))
            .RequireAuthorization(Policies.UserOrPrimaryToken).Audited("vm.hardware");
        api.MapPut("/vms/{name}/media", (string name, JsonElement body, HttpContext http, CancellationToken ct) => ChangeAsync(name, body, http, true, ct))
            .RequireAuthorization(Policies.UserOrPrimaryToken).Audited("vm.media");
        return api;
    }
    private static async Task<IResult> ChangeAsync(string name, JsonElement body, HttpContext http, bool mediaChange, CancellationToken ct)
    {
        var s = http.RequestServices; var vms = s.GetRequiredService<IVmRepository>();
        var vm = await vms.GetAsync(name, ct); if (vm is null) return Problems.NotFound("Unknown VM.");
        if (await LifecycleEndpoints.AuthorizeAsync(vm, http, true, ct) is { } denied) return denied;
        if (vm.Kind != VmKind.Child) return LifecycleEndpoints.Problem("not-a-child");
        var gates = s.GetRequiredService<IVmOperationGate>();
        await using var gate = await gates.TryAcquireAsync(name, http.TraceIdentifier, ct);
        if (gate is null) { gates.IsHeld(name, out var operation); return LifecycleEndpoints.Busy(operation); }
        vm = await vms.GetAsync(name, ct); if (vm is null) return Problems.NotFound("Unknown VM.");
        if (await LifecycleEndpoints.AuthorizeAsync(vm, http, true, ct) is { } refused) return refused;
        if (vm.Kind != VmKind.Child) return LifecycleEndpoints.Problem("not-a-child");
        var driver = s.GetRequiredService<IChildVmDriver>(); var keys = s.GetRequiredService<IOperationKeyStore>();
        var clock = s.GetRequiredService<IClock>(); var media = s.GetRequiredService<IMediaStore>();
        var admission = s.GetRequiredService<IAdmissionStore>(); var handles = new List<IAsyncDisposable>();
        try
        {
            if (body.ValueKind != JsonValueKind.Object) return CodedProblems.Validation("body", "Expected an object.");
            foreach (var field in new[] { "operationKey", "installMediaId", "auxiliaryMediaId" })
                if (body.TryGetProperty(field, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    return CodedProblems.Validation(field, "Expected a string or null.");
            var kind = mediaChange ? "child-media" : "child-hardware";
            var supplied = http.Request.Headers["X-Construct-Operation-Key"].FirstOrDefault() ??
                (body.TryGetProperty("operationKey", out var suppliedJson) ? suppliedJson.GetString() : null);
            if (supplied is not null && !OperationFingerprint.ValidKey(supplied)) return CodedProblems.Validation("operationKey", "Invalid operation key.");
            var fingerprint = OperationFingerprint.Compute(http.Request.Path, body);
            var key = supplied is null ? null : await keys.GetAsync(vm.Owner, kind, supplied, ct);
            if (key is not null && (key.Fingerprint != fingerprint || !Ownership.SameName(key.Target, name))) return LifecycleEndpoints.Problem("operation-key-conflict");
            if (key?.State == OperationKeyState.Completed) return Results.Json(JsonSerializer.Deserialize<JsonElement>(key.ResponseJson!, ApiJson.Options));
            if (await LifecycleEndpoints.LiveAsync(vm, s, ct)) return LifecycleEndpoints.Busy(vm.CurrentJobId);
            var pending = (await keys.ListInFlightAsync(name, ct)).Where(k => ConfigurationIntent.Applies(k, vm)).ToArray();
            if (pending.Any(k => k.Kind != kind || k.Fingerprint != fingerprint)) return LifecycleEndpoints.Problem("configuration-incomplete");
            key ??= pending.FirstOrDefault();
            if (await s.GetRequiredService<IHypervisorDriver>().GetStateAsync(name, ct) != VmState.Off) return LifecycleEndpoints.Problem("vm-not-off");
            if (vm.Incarnation is null || await driver.GetVmIdAsync(name, ct) != vm.Incarnation) return LifecycleEndpoints.Problem("vm-incarnation-conflict");
            var oldReferences = await media.ListReferencesForVmAsync(name, ct);
            var actual = await driver.GetAttachedMediaAsync(name, ct);
            if (!actual.Complete) return LifecycleEndpoints.Problem("media-unverified");
            async Task<string?> ResolveCurrent(string? path, MediaSlot slot)
            {
                if (path is null) return null;
                if (await s.GetRequiredService<IHypervisorDriver>().GetStateAsync(name, ct) != VmState.Off)
                    throw new LifecycleException("configuration-unverified");
                foreach (var reference in oldReferences.Where(r => r.Slot == slot))
                    if (await media.GetAsync(reference.MediaId, ct) is { } item && SamePath(item.Path, path)) return item.Id;
                throw new LifecycleException("media-unverified");
            }
            var install = await ResolveCurrent(actual.InstallPath, MediaSlot.Install);
            var auxiliary = await ResolveCurrent(actual.AuxiliaryPath, MediaSlot.Auxiliary);
            Intent intent;
            if (key is not null)
            {
                intent = JsonSerializer.Deserialize<Intent>(key.IntentJson!, ApiJson.Options)!;
                if (intent.Incarnation != vm.Incarnation) return LifecycleEndpoints.Problem("vm-incarnation-conflict");
            }
            else
            {
                var hardware = vm.Hardware!;
                if (mediaChange)
                {
                    if (body.TryGetProperty("installMediaId", out var i)) install = i.ValueKind == JsonValueKind.Null ? null : i.GetString();
                    if (body.TryGetProperty("auxiliaryMediaId", out var a)) auxiliary = a.ValueKind == JsonValueKind.Null ? null : a.GetString();
                    var order = body.TryGetProperty("bootOrder", out var b) ? b.Deserialize<BootDevice[]>(ApiJson.Options) : null;
                    hardware = hardware with { BootOrder = BootOrderRules.Resolve(order ?? hardware.BootOrder.Where(d => d != BootDevice.AuxiliaryMedia || auxiliary is not null).ToArray(), auxiliary is not null, hardware.NetworkAttached) };
                }
                else
                {
                    var request = body.Deserialize<HardwareRequest>(ApiJson.Options)!;
                    if (body.TryGetProperty("dynamicMemory", out _) || body.TryGetProperty("generation", out _) || body.TryGetProperty("networkAttached", out _))
                        return LifecycleEndpoints.Problem("unsupported-capability");
                    if (request.DiskGb < hardware.DiskGb) return CodedProblems.Validation("diskGb", "Disk shrink is not supported.");
                    if (request.DiskGb is int disk && disk != hardware.DiskGb) return LifecycleEndpoints.Problem("unsupported-capability");
                    hardware = hardware with { Cpus = request.Cpus ?? hardware.Cpus, RamMb = request.RamMb ?? hardware.RamMb,
                        SecureBoot = request.Firmware?.SecureBoot ?? hardware.SecureBoot,
                        SecureBootTemplate = request.Firmware?.SecureBootTemplate ?? hardware.SecureBootTemplate,
                        Tpm = request.Firmware?.Tpm ?? hardware.Tpm,
                        BootOrder = BootOrderRules.Resolve(request.Firmware?.BootOrder ?? hardware.BootOrder, auxiliary is not null, hardware.NetworkAttached) };
                }
                intent = new(hardware, install, auxiliary, hardware.SecureBootTemplate != vm.Hardware!.SecureBootTemplate, vm.Incarnation);
            }
            HardwarePresets.ValidateCapabilities(intent.Hardware, await driver.GetCapabilitiesAsync(ct), intent.AuxiliaryId is not null);
            var capacityConfig = await HostAdminEndpoints.CapacityConfigAsync(s.GetRequiredService<IHostConfigStore>(), s.GetRequiredService<Constructd.Core.Configuration.ConstructdOptions>(), ct);
            if (capacityConfig.MaxVcpusPerVm is int max && intent.Hardware.Cpus > max) return CodedProblems.Validation("cpus", "CPU count exceeds the host per-VM limit.");
            var capabilities = await driver.GetVmCapabilitiesAsync(name, ct);
            if (key is null && intent.TemplateChanged && capabilities.SecureBootTemplateLocked) return LifecycleEndpoints.Problem("template-locked");
            foreach (var id in oldReferences.Select(r => r.MediaId).Concat(new[] { intent.InstallId, intent.AuxiliaryId }.OfType<string>()).Distinct().Order(StringComparer.Ordinal))
                handles.Add(await s.GetRequiredService<IMediaGate>().AcquireAsync(id, http.TraceIdentifier, ct));
            var targetRefs = new List<MediaReference>(); var mediaResponse = new List<object>(); string? installPath = null, auxiliaryPath = null;
            foreach (var pair in new[] { (intent.InstallId, MediaSlot.Install), (intent.AuxiliaryId, MediaSlot.Auxiliary) })
            {
                if (pair.Item1 is null) continue;
                var item = await media.GetAsync(pair.Item1, ct);
                if (item is null) return Problems.NotFound("Unknown media.");
                if (!http.User.IsAdmin() && !Ownership.SameName(item.Owner, vm.Owner) || item.DedicatedTo is not null && !Ownership.SameName(item.DedicatedTo, name))
                    return LifecycleEndpoints.Problem("not-owner", 403);
                if (item.State != MediaState.Ready) return LifecycleEndpoints.Problem("media-not-ready");
                if (item.Role != (pair.Item2 == MediaSlot.Install ? MediaRole.Install : MediaRole.Auxiliary)) return CodedProblems.Validation("media", "Media role does not match slot.");
                if (pair.Item2 == MediaSlot.Install) installPath = item.Path; else auxiliaryPath = item.Path;
                targetRefs.Add(new(item.Id, name, pair.Item2, clock.UtcNow));
                mediaResponse.Add(new { item.Id, item.Role, item.Name, item.SizeBytes, item.State, dedicated = item.DedicatedTo is not null });
            }
            key ??= new(vm.Owner, kind, supplied ?? Guid.NewGuid().ToString("n"), fingerprint, name, null, OperationKeyState.InFlight,
                JsonSerializer.Serialize(intent, ApiJson.Options), vm.PowerGeneration, null, clock.UtcNow);
            using var operationHandle = s.GetRequiredService<IOperationRegistry>().Register("configuration:" + http.TraceIdentifier, kind, name);
            var accepted = await admission.AdmitAsync(new(key, null, null, [], [], targetRefs, null, null, null, null, null, false), ct);
            if (accepted.Outcome is not (AdmissionOutcome.Accepted or AdmissionOutcome.Replay)) return LifecycleEndpoints.Problem(accepted.Outcome switch
            {
                AdmissionOutcome.KeyConflict => "operation-key-conflict",
                AdmissionOutcome.VersionConflict => "operation-in-progress",
                AdmissionOutcome.MediaNotReady => "media-not-ready",
                _ => "operation-key-conflict"
            });
            CodedProblems.Audit(http, mediaChange ? "vm.media" : "vm.hardware", vm.Owner, vm.Parent, name, "operation=" + key.Key);
            if (mediaChange) await driver.SetMediaAsync(name, installPath, auxiliaryPath, intent.Hardware.BootOrder, ct);
            else await driver.UpdateHardwareAsync(name, intent.Hardware, intent.TemplateChanged && !capabilities.SecureBootTemplateLocked, ct);
            var confirmed = await driver.GetAttachedMediaAsync(name, ct);
            if (!confirmed.Complete || !SamePath(confirmed.InstallPath, installPath) || !SamePath(confirmed.AuxiliaryPath, auxiliaryPath)) return LifecycleEndpoints.Problem("media-unverified");
            if (await s.GetRequiredService<IHypervisorDriver>().GetStateAsync(name, ct) != VmState.Off)
                throw new LifecycleException("configuration-unverified");
            foreach (var reference in oldReferences.Where(r => !targetRefs.Any(t => t.MediaId == r.MediaId && t.Slot == r.Slot)))
                await media.RemoveReferenceAsync(reference.MediaId, name, reference.Slot, ct);
            object response = mediaChange ? mediaResponse : intent.Hardware;
            var completed = await admission.MutateAsync(key, async scope =>
            {
                if (!await scope.UpdateHardwareAsync(name, intent.Hardware, vm.PowerGeneration)) return false;
                return await scope.CompleteOperationKeyAsync(key.Owner, key.Kind, key.Key, JsonSerializer.Serialize(response, ApiJson.Options));
            }, ct);
            return completed.Outcome == AdmissionOutcome.Accepted ? Results.Ok(response) : LifecycleEndpoints.Problem("operation-key-conflict", extra: new() { ["reason"] = "power-state-changed" });
        }
        catch (JsonException) { return CodedProblems.Validation("body", "Invalid configuration request."); }
        catch (ChildValidationException ex) { return ex.Code == "validation" ? CodedProblems.Validation(ex.Field, "Invalid value.") : LifecycleEndpoints.Problem(ex.Code); }
        catch (LifecycleException ex) { return LifecycleEndpoints.Problem(ex.Code); }
        finally { foreach (var handle in handles.AsEnumerable().Reverse()) await handle.DisposeAsync(); }
    }
    private static bool SamePath(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
