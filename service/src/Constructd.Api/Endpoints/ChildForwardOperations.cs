using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Microsoft.AspNetCore.Authorization;
namespace Constructd.Api.Endpoints;

internal static class ChildForwardOperations
{
    public static async Task<IResult> CreateAsync(Vm vm, CreateForwardRequest? request, HttpContext http, CancellationToken ct)
    {
        var sp = http.RequestServices;
        var vms = sp.GetRequiredService<IVmRepository>();
        await using var gate = await sp.GetRequiredService<IVmOperationGate>().AcquireAsync(vm.Name, http.TraceIdentifier, ct);
        vm = (await vms.GetAsync(vm.Name, ct))!;
        if (vm is null || vm.Deleting) return CodedProblems.Create(409, "vm-deleting", "Target is unavailable.");
        var relationship = await DelegationAuthorization.RelationshipAsync(http.User, vm, vms, sp.GetRequiredService<IUserStore>(), ct);
        if (relationship is null) return Problems.Forbidden("Forward relationship refused.");
        if (request?.VmPort is not int port || port is < 1 or > 65535 || request.ConnectPort is < 1 or > 65535)
            return CodedProblems.Validation("vmPort", "Ports must be between 1 and 65535.");
        var target = ForwardTarget.Client;
        if (!string.IsNullOrWhiteSpace(request.Target) && !ApiHelpers.TryParseEnum(request.Target, out target))
            return CodedProblems.Validation("target", "Expected client or host.");
        string? via = null;
        if (target == ForwardTarget.Client)
        {
            var candidates = (await vms.ListAsync(null, ct)).Where(v => v.Kind == VmKind.Primary && !v.Deleting &&
                (http.User.IsPrimaryToken() ? Ownership.SameName(v.Name, http.User.VmTokenName()) : Ownership.SameName(v.Owner, http.User.NameOrEmpty()))).ToArray();
            if (relationship == ForwardRelationship.Admin && !Ownership.SameName(vm.Owner, http.User.NameOrEmpty()) && request.Via is null)
                return CodedProblems.Validation("via", "An administrator accessing another owner's child must name their primary.");
            if (request.Via is null && candidates.Length != 1)
                return CodedProblems.Validation("via", "Name one of your primary VMs to carry the tunnel.");
            via = request.Via is null ? candidates[0].Name : candidates.FirstOrDefault(v => Ownership.SameName(v.Name, request.Via))?.Name;
            if (via is null) return Problems.Forbidden("The via VM must be the requester's primary.");
        }
        var options = sp.GetRequiredService<ConstructdOptions>();
        var actor = ForwardRequesterHandler.Requester(http.User);
        var result = await sp.GetRequiredService<IAccessExposure>().TryExposeAsync(new(actor, relationship.Value, vm.Name, via,
            target, port, request.ConnectPort ?? port, request.Label?.Trim() ?? "", options.MaxForwardsPerVm), ct);
        CodedProblems.Audit(http, "forward.add", vm.Owner, vm.Parent, vm.Name, "requester=" + actor + ";relationship=" + relationship + ";target=" + target + ";result=" + result.Status);
        return result.Status switch
        {
            ExposeStatus.Added => Results.Created($"/api/v1/vms/{vm.Name}/forwards/{result.Forward!.Id}", ForwardResponse.From(result.Forward, options.PublicHostFor(vm.Name))),
            ExposeStatus.PolicyDenied => CodedProblems.Create(403, "host-forwards-disabled", "Host forwarding is disabled by host or owner policy."),
            ExposeStatus.AddressUnverifiable => Results.Problem(statusCode: 409, title: "Child destination unavailable",
                type: "urn:construct:problem:" + (result.Detail == "address-conflict" ? "address-conflict" : "address-unverifiable"),
                extensions: new Dictionary<string, object?> { ["code"] = result.Detail == "address-conflict" ? "address-conflict" : "address-unverifiable", ["reason"] = result.Detail, ["address"] = null }),
            ExposeStatus.LimitReached => Problems.Forbidden("The target has reached its forward limit."),
            _ => CodedProblems.Create(409, "vm-deleting", "Target is unavailable.")
        };
    }
    public static async Task<bool> MayRemoveAsync(HttpContext http, Vm vm, PortForward f, CancellationToken ct)
    {
        var relationship = await DelegationAuthorization.RelationshipAsync(http.User, vm, http.RequestServices.GetRequiredService<IVmRepository>(), http.RequestServices.GetRequiredService<IUserStore>(), ct);
        return relationship is ForwardRelationship.Admin or ForwardRelationship.Owner or ForwardRelationship.Parent ||
            relationship is not null && Ownership.SameName(f.Destination?.RequestedBy, ForwardRequesterHandler.Requester(http.User));
    }
    public static async Task<bool> MayAckAsync(HttpContext http, Vm vm, PortForward f, CancellationToken ct)
    {
        if (!http.User.IsKnownUser()) return false;
        if (http.User.IsAdmin()) return true;
        var vms = http.RequestServices.GetRequiredService<IVmRepository>();
        if (f.Destination?.Via is not { } via || await vms.GetAsync(via, ct) is not { Kind: VmKind.Primary, Deleting: false } primary ||
            !Ownership.SameName(primary.Owner, http.User.NameOrEmpty())) return false;
        return await DelegationAuthorization.RelationshipAsync(http.User, vm, vms, http.RequestServices.GetRequiredService<IUserStore>(), ct) is not null;
    }
}
