using Constructd.Api.Auth;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Api.Endpoints;

public static class PrimaryCpuEndpoints
{
    public sealed record Request(int Cpus);
    public static RouteGroupBuilder MapPrimaryCpuEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/vm-defaults", async (HttpContext http, PrimaryCpuSettings settings, CancellationToken ct) =>
        {
            try { return Results.Ok(await settings.LimitsAsync(http.User.NameOrEmpty(), null, ct)); }
            catch (LifecycleException ex) { return LifecycleEndpoints.Problem(ex.Code); }
        }).RequireAuthorization(Policies.User);
        api.MapGet("/vms/{name}/cpu", (string name, HttpContext http, CancellationToken ct) => ReadOrSave(name, null, http, ct))
            .RequireAuthorization(Policies.User);
        api.MapPut("/vms/{name}/cpu", (string name, Request request, HttpContext http, CancellationToken ct) => ReadOrSave(name, request, http, ct))
            .RequireAuthorization(Policies.User).Audited("vm.cpu");
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
            var settings = services.GetRequiredService<PrimaryCpuSettings>();
            var limits = await settings.LimitsAsync(vm.Owner, vm, ct);
            if (request is not null)
            {
                if (request.Cpus < 1 || request.Cpus > limits.MaximumCpus)
                    return CodedProblems.Validation("cpus", $"CPU count must be between 1 and {limits.MaximumCpus} for this VM's owner and host.");
                await settings.SaveAsync(vm, request.Cpus, http.User.Actor(), ct);
                CodedProblems.Audit(http, "vm.cpu", vm.Owner, target: name, extra: "cpus=" + request.Cpus);
            }
            var desired = (await settings.GetAsync(vm, ct))?.Cpus ?? vm.Cpu;
            return Results.Ok(new { currentCpus = vm.Cpu, desiredCpus = desired, pending = desired != vm.Cpu,
                limits.HostLogicalCpus, limits.MaximumCpus, limits.RecommendedCpus, appliesOn = "next-stop-start" });
        }
        catch (LifecycleException ex) { return LifecycleEndpoints.Problem(ex.Code); }
    }
}
