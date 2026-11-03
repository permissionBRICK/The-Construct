using Constructd.Api.Auth;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Api.Endpoints;

/// <summary>
/// The minimum RAM a primary may be shrunk to under host memory pressure (Proxmox only). Owners can read
/// it; only admins set the per-VM override, because it decides how the host's RAM is shared.
/// </summary>
public static class PrimaryMemoryFloorEndpoints
{
    /// <summary><c>MinimumRamGb</c>: null follows the host default, 0 never shrinks the VM.</summary>
    public sealed record Request(int? MinimumRamGb);
    public static RouteGroupBuilder MapPrimaryMemoryFloorEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/vms/{name}/memory-floor", (string name, HttpContext http, CancellationToken ct) => ReadOrSave(name, null, http, ct))
            .RequireAuthorization(Policies.User);
        api.MapPut("/vms/{name}/memory-floor", (string name, Request request, HttpContext http, CancellationToken ct) => ReadOrSave(name, request, http, ct))
            .RequireAuthorization(Policies.Admin).Audited("vm.memory-floor");
        return api;
    }

    private static async Task<IResult> ReadOrSave(string name, Request? request, HttpContext http, CancellationToken ct)
    {
        var services = http.RequestServices;
        var vms = services.GetRequiredService<IVmRepository>();
        var floor = services.GetRequiredService<PrimaryMemoryFloor>();
        var vm = await vms.GetAsync(name, ct);
        if (vm is null) return Problems.NotFound("Unknown VM.");
        if (await LifecycleEndpoints.AuthorizeAsync(vm, http, true, ct) is { } denied) return denied;
        if (vm.Kind != VmKind.Primary) return LifecycleEndpoints.Problem("not-a-primary");
        if (!floor.Supported) return LifecycleEndpoints.Problem("unsupported-on-host");
        var admin = http.User.IsAdmin();
        if (request is null) return Results.Ok(await floor.ProjectAsync(vm, admin, ct));
        if (request.MinimumRamGb is < 0 or > 1024)
            return CodedProblems.Validation("minimumRamGb", "Minimum RAM (GB) must be null (host default), 0 (never shrink) or 1 to 1024.");
        await using var gate = await PrimaryOperationGate.AcquireAsync(services.GetRequiredService<IVmOperationGate>(), name, http.TraceIdentifier, ct);
        if (gate is null) return LifecycleEndpoints.Busy(vm.CurrentJobId);
        vm = await vms.GetAsync(name, ct);
        if (vm is null || vm.Deleting) return Problems.NotFound("Unknown VM.");
        await floor.SaveAsync(vm, request.MinimumRamGb, http.User.Actor(), ct);
        CodedProblems.Audit(http, "vm.memory-floor", vm.Owner, target: name,
            extra: "minimumRamGb=" + (request.MinimumRamGb?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "host"));
        try { await floor.ApplyAsync(vm, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Saved: the next start or service start retries.
            services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(PrimaryMemoryFloorEndpoints))
                .LogWarning(ex, "Saved the minimum RAM of {VmName} but could not apply it", vm.Name);
            return LifecycleEndpoints.Problem("memory-floor-apply-failed");
        }
        return Results.Ok(await floor.ProjectAsync(vm, admin, ct));
    }
}
