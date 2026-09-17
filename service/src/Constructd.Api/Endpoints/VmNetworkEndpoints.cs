using System.Text.Json;
using Constructd.Api.Auth;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Api.Endpoints;

public static class VmNetworkEndpoints
{
    public static RouteGroupBuilder MapVmNetworkEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/vms/{name}/network", (string name, HttpContext http, CancellationToken ct) => ReadOrSave(name, null, http, ct))
            .RequireAuthorization(Policies.User);
        api.MapPut("/vms/{name}/network", (string name, JsonElement request, HttpContext http, CancellationToken ct) => ReadOrSave(name, request, http, ct))
            .RequireAuthorization(Policies.User).Audited("vm.network");
        return api;
    }

    private static async Task<IResult> ReadOrSave(string name, JsonElement? request, HttpContext http, CancellationToken ct)
    {
        var services = http.RequestServices;
        var vms = services.GetRequiredService<IVmRepository>();
        var settings = services.GetRequiredService<VmNetworkSettings>();
        var vm = await vms.GetAsync(name, ct);
        if (vm is null) return Problems.NotFound("Unknown VM.");
        if (await LifecycleEndpoints.AuthorizeAsync(vm, http, true, ct) is { } denied) return denied;
        if (!settings.Supported) return CodedProblems.Create(400, "unsupported-on-platform", "Network mode requires Proxmox.");
        if (vm.Kind != VmKind.Primary) return LifecycleEndpoints.Problem("not-a-primary");
        if (request is null) return Results.Ok(await settings.ProjectAsync(vm, http.User.IsAdmin(), ct));

        await using var gate = await PrimaryOperationGate.AcquireAsync(services.GetRequiredService<IVmOperationGate>(), name, http.TraceIdentifier, ct);
        if (gate is null) return LifecycleEndpoints.Busy(vm.CurrentJobId);
        vm = await vms.GetAsync(name, ct);
        if (vm is null) return Problems.NotFound("Unknown VM.");
        if (await LifecycleEndpoints.AuthorizeAsync(vm, http, true, ct) is { } refused) return refused;
        if (await LifecycleEndpoints.LiveAsync(vm, services, ct) || (await services.GetRequiredService<IOperationKeyStore>().ListInFlightAsync(name, ct)).Any())
            return LifecycleEndpoints.Problem("operation-in-progress");
        if (ApiHelpers.FenceDeleting(vm) is { } fence) return fence;
        CodedProblems.Audit(http, "vm.network", vm.Owner, target: name);
        var body = request.Value;
        if (body.ValueKind != JsonValueKind.Object) return CodedProblems.Validation("network", "Expected an object.");
        var seen = new HashSet<string>();
        var setting = await settings.GetAsync(vm, ct) ?? new(vm.Created, null, null, null, null);
        foreach (var field in body.EnumerateObject())
        {
            if (!seen.Add(field.Name) || field.Name is not ("mode" or "address" or "gateway" or "dns"))
                return CodedProblems.Validation(field.Name, "Unknown or duplicate field.");
            if (!http.User.IsAdmin() && (field.Name != "mode" || !(await services.GetRequiredService<IHostNetworkPolicy>().GetAsync(ct)).OwnerMaySwitchMode))
                return CodedProblems.Create(403, "policy-denied", "The host policy does not allow this network setting.");
            try
            {
                setting = field.Name switch
                {
                    "mode" => setting with { Mode = field.Value.GetString() },
                    "address" => setting with { Address = field.Value.GetString() },
                    "gateway" => setting with { Gateway = field.Value.GetString() },
                    "dns" => setting with { Dns = field.Value.Deserialize<string[]>() },
                    _ => setting
                };
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            { return CodedProblems.Validation(field.Name, "Invalid value."); }
        }
        if (VmNetworkSettings.Validate(setting) is { } error) return CodedProblems.Validation("network", error);
        await settings.SaveAsync(vm, setting, http.User.Actor(), ct);
        return Results.Ok(await settings.ProjectAsync(vm, http.User.IsAdmin(), ct));
    }
}
