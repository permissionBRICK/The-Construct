using Constructd.Api.Auth;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Api.Endpoints;

public static class PrimaryMemoryEndpoints
{
    public sealed record Request(int RamGb);
    public static RouteGroupBuilder MapPrimaryMemoryEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/vms/{name}/memory", (string name, HttpContext http, CancellationToken ct) => ReadOrSave(name, null, http, ct))
            .RequireAuthorization(Policies.User);
        api.MapPut("/vms/{name}/memory", (string name, Request request, HttpContext http, CancellationToken ct) => ReadOrSave(name, request, http, ct))
            .RequireAuthorization(Policies.User).Audited("vm.memory");
        return api;
    }

    private static async Task<IResult> ReadOrSave(string name, Request? request, HttpContext http, CancellationToken ct)
    {
        var services = http.RequestServices;
        var vms = services.GetRequiredService<IVmRepository>();
        var vm = await vms.GetAsync(name, ct);
        if (vm is null) return Problems.NotFound("Unknown VM.");
        if (await LifecycleEndpoints.AuthorizeAsync(vm, http, true, ct) is { } denied) return denied;
        if (vm.Kind != VmKind.Primary) return LifecycleEndpoints.Problem("not-a-primary");
        await using var gate = await services.GetRequiredService<IVmOperationGate>().TryAcquireAsync(name, http.TraceIdentifier, ct);
        if (gate is null) return LifecycleEndpoints.Busy(vm.CurrentJobId);
        vm = await vms.GetAsync(name, ct);
        if (vm is null) return Problems.NotFound("Unknown VM.");
        if (await LifecycleEndpoints.AuthorizeAsync(vm, http, true, ct) is { } refused) return refused;
        if (vm.Kind != VmKind.Primary) return LifecycleEndpoints.Problem("not-a-primary");
        if (await LifecycleEndpoints.LiveAsync(vm, services, ct)) return LifecycleEndpoints.Busy(vm.CurrentJobId);
        if (request is not null && (await services.GetRequiredService<IOperationKeyStore>().ListInFlightAsync(vm.Name, ct)).Any())
            return LifecycleEndpoints.Problem("operation-in-progress");
        try
        {
            var settings = services.GetRequiredService<PrimaryMemorySettings>();
            var limits = await settings.LimitsAsync(vm.Owner, vm, ct);
            if (request is not null)
            {
                if (request.RamGb < 1 || request.RamGb > limits.MaximumRamGb)
                    return CodedProblems.Validation("ramGb", $"RAM (GB) must be between 1 and {limits.MaximumRamGb} for this VM's owner and host.");
                await settings.SaveAsync(vm, request.RamGb, http.User.Actor(), ct);
                CodedProblems.Audit(http, "vm.memory", vm.Owner, target: name, extra: "ramGb=" + request.RamGb);
            }
            var desired = (await settings.GetAsync(vm, ct))?.RamGb ?? vm.RamGb;
            return Results.Ok(new { currentRamGb = vm.RamGb, desiredRamGb = desired, pending = desired != vm.RamGb,
                limits.MaximumRamGb, limits.RecommendedRamGb, appliesOn = "next-stop-start" });
        }
        catch (LifecycleException ex) { return LifecycleEndpoints.Problem(ex.Code); }
    }
}
