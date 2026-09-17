using Constructd.Api.Auth;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Api.Endpoints;

public static class PrimaryNestedEndpoints
{
    public sealed record Request(bool? Enabled);
    public static RouteGroupBuilder MapPrimaryNestedEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/vms/{name}/nested", (string name, HttpContext http, CancellationToken ct) => ReadOrSave(name, null, http, ct))
            .RequireAuthorization(Policies.User);
        api.MapPut("/vms/{name}/nested", (string name, Request request, HttpContext http, CancellationToken ct) => ReadOrSave(name, request, http, ct))
            .RequireAuthorization(Policies.User).Audited("vm.nested");
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
        var settings = services.GetRequiredService<PrimaryNestedSettings>();
        var driver = services.GetRequiredService<IVmNestedDriver>();
        var hypervisor = services.GetRequiredService<IHypervisorDriver>();
        var config = await services.GetRequiredService<IHostConfigStore>().GetAsync<VirtualizationConfig>("virtualization", ct) ?? HostAdminDefaults.Virtualization;
        var user = await services.GetRequiredService<IUserStore>().GetAsync(http.User.NameOrEmpty(), ct);
        var selectable = user is not null && NestedPolicy.MaySelect(user, config);
        async Task<IResult> Read(Vm currentVm)
        {
            var current = await driver.GetNestedAsync(currentVm.Name, ct);
            var desired = (await settings.GetAsync(currentVm, ct))?.Enabled ?? current;
            return Results.Ok(new { current, desired, pending = current != desired, available = hypervisor.NestedAvailable,
                selectable, appliesOn = "next-stop-start" });
        }
        if (request is null) return await Read(vm);
        if (request.Enabled is not bool enabled) return CodedProblems.Validation("enabled", "A boolean is required.");
        if (enabled && !selectable) return CodedProblems.Create(403, "policy-denied", "Nested virtualization selection is disabled for this user.");
        if (enabled && !hypervisor.NestedAvailable) return LifecycleEndpoints.Problem("unsupported-on-host");
        await using var gate = await PrimaryOperationGate.AcquireAsync(services.GetRequiredService<IVmOperationGate>(), name, http.TraceIdentifier, ct);
        if (gate is null) return LifecycleEndpoints.Busy(vm.CurrentJobId);
        vm = await vms.GetAsync(name, ct);
        if (vm is null) return Problems.NotFound("Unknown VM.");
        if (await LifecycleEndpoints.AuthorizeAsync(vm, http, true, ct) is { } refused) return refused;
        if (vm.Kind != VmKind.Primary) return LifecycleEndpoints.Problem("not-a-primary");
        if (await LifecycleEndpoints.LiveAsync(vm, services, ct) || (await services.GetRequiredService<IOperationKeyStore>().ListInFlightAsync(name, ct)).Any())
            return LifecycleEndpoints.Problem("operation-in-progress");
        await settings.SaveAsync(vm, enabled, http.User.Actor(), ct);
        await settings.ApplyAsync(vm, await hypervisor.GetStateAsync(name, ct), ct);
        CodedProblems.Audit(http, "vm.nested", vm.Owner, target: name, extra: "enabled=" + enabled);
        return await Read(vm);
    }
}
