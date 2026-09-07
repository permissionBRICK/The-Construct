using System.Text.Json;
using Constructd.Api.Auth;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Microsoft.AspNetCore.Authorization;
namespace Constructd.Api.Endpoints;

public static class LifecycleEndpoints
{
    public static RouteGroupBuilder MapLifecycleEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/vms/{name}/lifecycle", LifecycleAsync).RequireAuthorization(Policies.UserOrPrimaryToken).Audited("vm.lifecycle").WithName("VmLifecycle");
        api.MapPost("/vms/{name}/lease", RenewAsync).RequireAuthorization(Policies.UserOrPrimaryToken).Audited("vm.lease.renew").WithName("RenewChildLease");
        api.MapPut("/vms/{name}/sharing", ShareAsync).RequireAuthorization(Policies.UserOrPrimaryToken).Audited("vm.share").WithName("ShareChildVm");
        return api;
    }
    private sealed record Request(string? Action = null, string? Lifetime = null, string? OperationKey = null, string? Scope = null);
    internal static IResult Problem(string code, int status = 409, Dictionary<string, object?>? extra = null) =>
        CodedProblems.Create(status, code, "VM operation refused: " + code.Replace('-', ' ') + ".", extra: extra);
    internal static IResult Busy(string? jobId) => Problem("operation-in-progress", extra: new() { ["jobId"] = jobId });
    private static OperationKeyRecord Key(Vm vm, string kind, JsonElement body, HttpContext http, string? bodyKey, IClock clock)
    {
        var supplied = http.Request.Headers["X-Construct-Operation-Key"].FirstOrDefault() ?? bodyKey;
        if (supplied is not null && !OperationFingerprint.ValidKey(supplied)) throw new ChildValidationException("validation", "operationKey");
        return new(vm.Owner, kind, supplied ?? Guid.NewGuid().ToString("n"), OperationFingerprint.Compute(http.Request.Path, body),
            vm.Name, null, OperationKeyState.InFlight, null, vm.PowerGeneration, null, clock.UtcNow);
    }
    private static IResult LifetimeDenied(string requested, EffectiveAllowance allowance) => Problem("lifetime-not-allowed", 403,
        new() { ["requested"] = requested, ["allowedMax"] = allowance.MaxChildLifetimeSeconds, ["allowNever"] = allowance.AllowNeverLifetime });
    private static bool Allowed(long? seconds, EffectiveAllowance allowance) => seconds is null ? allowance.AllowNeverLifetime :
        allowance.MaxChildLifetimeSeconds is not long max || seconds <= max;
    internal static async Task<bool> LiveAsync(Vm vm, IServiceProvider services, CancellationToken ct) => vm.CurrentJobId is not null &&
        await services.GetRequiredService<IJobStore>().GetAsync(vm.CurrentJobId, ct) is { State: JobState.Queued or JobState.Running };
    internal static async Task<IResult?> AuthorizeAsync(Vm vm, HttpContext http, bool ownerOnly, CancellationToken ct)
    {
        var policy = vm.Kind == VmKind.Primary ? Policies.VmOwnerOrAdmin : ownerOnly ? Policies.ChildOwnerOrAdmin : Policies.ChildOperator;
        if (!(await http.RequestServices.GetRequiredService<IAuthorizationService>().AuthorizeAsync(http.User, vm, policy)).Succeeded)
            return Problem("not-owner", 403);
        if (vm.Deleting) return Problem("vm-deleting");
        if (vm.Parent is string parent && await http.RequestServices.GetRequiredService<IVmRepository>().GetAsync(parent, ct) is not { Deleting: false, ChildCreationClosed: false })
            return Problem("parent-closed");
        return null;
    }
    private static async Task<IResult> LifecycleAsync(string name, JsonElement body, HttpContext http, IVmRepository vms,
        IVmOperationGate gate, IClock clock, CancellationToken ct)
    {
        var vm = await vms.GetAsync(name, ct); if (vm is null) return Problems.NotFound("Unknown VM.");
        if (await AuthorizeAsync(vm, http, false, ct) is { } denied) return denied;
        await using var held = await gate.TryAcquireAsync(vm.Name, http.TraceIdentifier, ct);
        if (held is null) { gate.IsHeld(vm.Name, out var operation); return Busy(operation); }
        vm = (await vms.GetAsync(vm.Name, ct))!;
        if (await AuthorizeAsync(vm, http, false, ct) is { } refused) return refused;
        var s = http.RequestServices;
        try
        {
            var request = body.Deserialize<Request>(ApiJson.Options) ?? new();
            if (request.Action is not ("start" or "shutdown" or "restart" or "save")) return CodedProblems.Validation("action", "Expected start, shutdown, restart or save.");
            if (vm.Kind == VmKind.Primary && request.Lifetime is not null) return CodedProblems.Validation("lifetime", "Primaries have no lease.");
            long? seconds = null;
            if (request.Action == "start" && vm.Kind == VmKind.Child)
            {
                if (request.Lifetime is null) return Problem("lifetime-required", 400);
                seconds = LifetimeParser.Parse(request.Lifetime, clock.UtcNow);
                var allowance = await s.GetRequiredService<IDelegationPolicy>().ResolveAsync(vm.Owner, vm.Parent, ct);
                if (!Allowed(seconds, allowance)) return LifetimeDenied(request.Lifetime, allowance);
            }
            var key = Key(vm, "lifecycle-" + request.Action, body, http, request.OperationKey, clock);
            var existing = await s.GetRequiredService<IOperationKeyStore>().GetAsync(key.Owner, key.Kind, key.Key, ct);
            if (existing is not null && (existing.Fingerprint != key.Fingerprint || !Ownership.SameName(existing.Target, vm.Name))) return Problem("operation-key-conflict");
            if (existing?.State == OperationKeyState.Completed) return Replay(existing);
            if (await LiveAsync(vm, s, ct)) return Busy(vm.CurrentJobId);
            CodedProblems.Audit(http, "vm.lifecycle", vm.Owner, vm.Parent, vm.Name, "action=" + request.Action);
            if (request.Action == "start")
            {
                var reply = await s.GetRequiredService<LifecycleStart>().RunAsync(vm, request.Lifetime, seconds, key, ct);
                return reply.Code is null ? Results.Ok(new { state = reply.State, lease = reply.Lease }) : Problem(reply.Code);
            }
            if (request.Action == "save")
            {
                var driver = s.GetRequiredService<IHypervisorDriver>();
                if (!driver.Capabilities.Suspend) return Problem("unsupported-capability");
                if (await driver.GetStateAsync(vm.Name, ct) is var savedFrom && savedFrom is not (VmState.Running or VmState.Paused))
                    return Problem("vm-state-unknown", extra: new() { ["state"] = savedFrom, ["reason"] = "save-requires-running-or-paused" });
                await driver.SaveAsync(vm.Name, ct);
                var state = await driver.GetStateAsync(vm.Name, ct);
                if (state != VmState.Saved) return Problem("vm-state-unknown");
                var rows = (await s.GetRequiredService<ICapacityLedger>().SnapshotAsync(false, ct)).Reservations.Where(r => Ownership.SameName(r.VmName, vm.Name)).Select(r => r.Id).ToArray();
                var result = await s.GetRequiredService<IAdmissionStore>().MutateAsync(key, async scope =>
                {
                    if (!await scope.UpdatePowerStateAsync(vm.Name, state, vm.PowerGeneration)) return false;
                    await scope.ReleaseReservationsAsync(rows, state, "saved");
                    return await scope.CompleteOperationKeyAsync(key.Owner, key.Kind, key.Key, JsonSerializer.Serialize(new { state }, ApiJson.Options));
                }, ct);
                return result.Outcome == AdmissionOutcome.Accepted ? Results.Ok(new { state }) : Problem("operation-key-conflict");
            }
            if (request.Action == "restart" && (LeaseRules.Due(vm.Lease, clock.UtcNow) || vm.Lease?.State == LeaseState.Overdue)) return Problem("lease-due");
            if (request.Action == "restart" && await s.GetRequiredService<IHypervisorDriver>().GetStateAsync(vm.Name, ct) is var observed && observed != VmState.Running)
                return Problem("vm-state-unknown", extra: new() { ["state"] = observed, ["reason"] = "restart-requires-running" });
            var job = await s.GetRequiredService<LifecycleJobAdmission>().SubmitAsync(vm, http.User.Actor(), request.Action == "restart", null, key, ct);
            return Results.Accepted("/api/v1/jobs/" + job.Id, new { jobId = job.Id });
        }
        catch (JsonException) { return CodedProblems.Validation("body", "Invalid lifecycle request."); }
        catch (ChildValidationException ex) { return CodedProblems.Validation(ex.Field, "Invalid value."); }
        catch (LifecycleException ex)
        {
            if (ex.Capacity is { } d) return Problem(ex.Code, extra: new() { ["resource"] = d.Resource, ["scope"] = d.Scope,
                ["requested"] = d.Requested, ["allowed"] = d.AllowedAmount, ["available"] = d.Available, ["reason"] = d.Reason, ["epoch"] = d.Epoch });
            return Problem(ex.Code == "power-state-changed" ? "operation-key-conflict" : ex.Code, ex.Code == "maintenance" ? 503 : ex.Code == "job-start-failed" ? 500 : 409, extra: new() { ["reason"] = ex.Code });
        }
    }
    internal static IResult Replay(OperationKeyRecord key)
    {
        using var json = JsonDocument.Parse(key.ResponseJson ?? "{}");
        if (json.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String) return Problem(code.GetString()!);
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(key.ResponseJson ?? "{}", ApiJson.Options)!;
        var reply = values.ToDictionary(p => p.Key, p => (object?)p.Value); reply["replayed"] = true;
        return Results.Ok(reply);
    }
    private static Task<IResult> RenewAsync(string name, JsonElement body, HttpContext http, CancellationToken ct) => ChangeAsync(name, body, http, false, ct);
    private static Task<IResult> ShareAsync(string name, JsonElement body, HttpContext http, CancellationToken ct) => ChangeAsync(name, body, http, true, ct);
    private static async Task<IResult> ChangeAsync(string name, JsonElement body, HttpContext http, bool sharing, CancellationToken ct)
    {
        var s = http.RequestServices; var vms = s.GetRequiredService<IVmRepository>();
        var vm = await vms.GetAsync(name, ct); if (vm is null) return Problems.NotFound("Unknown VM.");
        if (await AuthorizeAsync(vm, http, true, ct) is { } denied) return denied;
        if (vm.Kind != VmKind.Child) return Problem("not-a-child");
        var gate = s.GetRequiredService<IVmOperationGate>();
        await using var held = await gate.TryAcquireAsync(vm.Name, http.TraceIdentifier, ct);
        if (held is null) { gate.IsHeld(vm.Name, out var operation); return Busy(operation); }
        vm = (await vms.GetAsync(vm.Name, ct))!;
        if (await AuthorizeAsync(vm, http, true, ct) is { } refused) return refused;
        if (await LiveAsync(vm, s, ct)) return Busy(vm.CurrentJobId);
        try
        {
            var request = body.Deserialize<Request>(ApiJson.Options) ?? new(); var clock = s.GetRequiredService<IClock>();
            var allowance = await s.GetRequiredService<IDelegationPolicy>().ResolveAsync(vm.Owner, vm.Parent, ct);
            Lease? lease = null; var scope = vm.Sharing;
            if (sharing)
            {
                if (request.Scope is not ("private" or "host")) return Problem("sharing-scope-unsupported", 400);
                scope = request.Scope == "host" ? SharingScope.Host : SharingScope.Private;
                if (!allowance.AllowSharing && scope == SharingScope.Host) return Problem("sharing-not-allowed", 403);
            }
            else
            {
                if (request.Lifetime is null) return Problem("lifetime-required", 400);
                var seconds = LifetimeParser.Parse(request.Lifetime, clock.UtcNow);
                if (!Allowed(seconds, allowance)) return LifetimeDenied(request.Lifetime, allowance);
                lease = LeaseRules.Activate(vm.Lease!, request.Lifetime, seconds, clock.UtcNow);
            }
            var key = Key(vm, sharing ? "sharing" : "lease-renew", body, http, request.OperationKey, clock);
            var prior = await s.GetRequiredService<IOperationKeyStore>().GetAsync(key.Owner, key.Kind, key.Key, ct);
            if (prior is not null && (prior.Fingerprint != key.Fingerprint || !Ownership.SameName(prior.Target, key.Target))) return Problem("operation-key-conflict");
            if (prior?.State == OperationKeyState.Completed)
            {
                if (sharing && vm.Sharing == SharingScope.Private) await RevokeSharedAsync(vm, s, ct);
                return Replay(prior);
            }
            if (!sharing && await s.GetRequiredService<IHypervisorDriver>().GetStateAsync(vm.Name, ct) != VmState.Running) return Problem("lease-inactive");
            var json = sharing ? JsonSerializer.Serialize(new { scope }, ApiJson.Options) : JsonSerializer.Serialize(lease, ApiJson.Options);
            var result = await s.GetRequiredService<IAdmissionStore>().MutateAsync(key, async tx =>
            {
                if (sharing ? !await tx.UpdateSharingAsync(vm.Name, scope) : !await tx.UpdateLeaseAsync(vm.Name, lease!, vm.Lease!.Version)) return false;
                return await tx.CompleteOperationKeyAsync(key.Owner, key.Kind, key.Key, json);
            }, ct);
            if (result.Outcome != AdmissionOutcome.Accepted) return Problem("operation-key-conflict");
            if (sharing)
            {
                if (scope == SharingScope.Private)
                {
                    await RevokeSharedAsync(vm, s, ct);
                }
                else await s.GetRequiredService<INetworkPolicyReconciler>().OnSharingChangedAsync(vm with { Sharing = scope }, vm.Sharing, ct);
            }
            CodedProblems.Audit(http, sharing ? "vm.share" : "vm.lease.renew", vm.Owner, vm.Parent, vm.Name);
            return Results.Content(json, "application/json");
        }
        catch (JsonException) { return CodedProblems.Validation("body", "Invalid request."); }
        catch (ChildValidationException ex) { return CodedProblems.Validation(ex.Field, "Invalid value."); }
    }
    private static async Task RevokeSharedAsync(Vm vm, IServiceProvider services, CancellationToken ct)
    {
        services.GetRequiredService<IConsoleSessionStore>().RemoveForVmExcept(vm.Name, [vm.Owner, "vm:" + vm.Parent]);
        var exposure = services.GetRequiredService<IAccessExposure>();
        if (exposure is not Constructd.Core.Services.UnsupportedFeaturePlatform) await exposure.RevokeNonOwnerAsync(vm.Name, ct);
        await services.GetRequiredService<INetworkPolicyReconciler>().OnSharingChangedAsync(vm with { Sharing = SharingScope.Private }, SharingScope.Host, ct);
    }
}
