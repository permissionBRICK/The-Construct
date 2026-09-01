using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Microsoft.AspNetCore.Authorization;

namespace Constructd.Api.Endpoints;

/// <summary>
/// Port forwards (plan §4.6). Reachable with a user credential OR with the VM's own scoped token,
/// which is what <c>construct expose</c> uses from inside the guest — and only ever for its own VM.
/// </summary>
public static class ForwardEndpoints
{
    public static RouteGroupBuilder MapForwardEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/vms/{name}/forwards", ListAsync)
            .RequireAuthorization(Policies.VmScoped).WithName("ListForwards");

        api.MapPost("/vms/{name}/forwards", CreateAsync)
            .RequireAuthorization(Policies.VmScoped).Audited("forward.add").WithName("CreateForward");

        api.MapDelete("/vms/{name}/forwards/{id}", DeleteAsync)
            .RequireAuthorization(Policies.VmScoped).Audited("forward.remove").WithName("DeleteForward");

        return api;
    }

    private static async Task<IResult> ListAsync(
        string name,
        HttpContext http,
        IVmRepository repository,
        IAuthorizationService authorization,
        IPortForwardManager forwards,
        ConstructdOptions options,
        CancellationToken cancellationToken)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, repository, authorization, name,
            Policies.VmSelfOrOwnerOrAdmin, cancellationToken).ConfigureAwait(false);

        if (!lookup.Ok)
        {
            return lookup.Failure!;
        }

        var list = await forwards.ListAsync(lookup.Vm!.Name, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(list.Select(f => ForwardResponse.From(f, options.PublicHost)).ToList());
    }

    private static async Task<IResult> CreateAsync(
        string name,
        CreateForwardRequest? request,
        HttpContext http,
        IVmRepository repository,
        IUserStore users,
        IAuthorizationService authorization,
        IPortForwardManager forwards,
        ConstructdOptions options,
        CancellationToken cancellationToken)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, repository, authorization, name,
            Policies.VmSelfOrOwnerOrAdmin, cancellationToken).ConfigureAwait(false);

        if (!lookup.Ok)
        {
            return lookup.Failure!;
        }

        var vm = lookup.Vm!;

        if (ApiHelpers.FenceDeleting(vm) is { } fenced)
        {
            http.SetAuditDetail("vm is being deleted");
            return fenced;
        }

        if (request?.VmPort is not int vmPort || vmPort is < 1 or > 65535)
        {
            return Problems.BadRequest("'vmPort' must be between 1 and 65535.");
        }

        // Client is the default target: private to the user's PC, no LAN exposure (plan §4.6).
        var target = ForwardTarget.Client;
        if (!string.IsNullOrWhiteSpace(request.Target) &&
            !ApiHelpers.TryParseEnum(request.Target, out target))
        {
            return Problems.BadRequest($"'target' must be one of: {ApiHelpers.Options<ForwardTarget>()}.");
        }

        var label = request.Label?.Trim() ?? string.Empty;

        if (target == ForwardTarget.Host)
        {
            // The policy follows the VM's OWNER, not the caller: an admin acting on someone else's VM
            // must not be able to route around that user's restriction.
            var owner = await users.GetAsync(vm.Owner, cancellationToken).ConfigureAwait(false);
            if (owner is null || !owner.AllowHostForwards)
            {
                http.SetAuditDetail("host forwards disabled for the owner");
                return Problems.Forbidden(
                    $"Host-target forwards are disabled for '{vm.Owner}'. Use target=client.");
            }
        }

        AddForwardResult result;
        try
        {
            // The cap, the "is this VM still there" check and the insert all happen inside the manager
            // under the VM's own gate: a guest could otherwise fire concurrent requests through the gap
            // between counting and adding, or attach a forward to a VM that is being torn down.
            result = await forwards.TryAddForwardAsync(
                vm.Name, vmPort, target, label, options.MaxForwardsPerVm, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PortRangeExhaustedException ex)
        {
            http.SetAuditDetail(ex.Message);
            return Problems.Conflict(ex.Message);
        }

        if (result.Status == AddForwardStatus.LimitReached)
        {
            http.SetAuditDetail($"limit {options.MaxForwardsPerVm} reached");
            return Problems.Forbidden(
                $"VM '{vm.Name}' already has the maximum of {options.MaxForwardsPerVm} forwards.");
        }

        if (result.Status == AddForwardStatus.VmUnavailable)
        {
            http.SetAuditDetail("vm is gone or being deleted");
            return Problems.Conflict($"VM '{vm.Name}' is being deleted.");
        }

        var forward = result.Forward!;
        http.SetAuditDetail(
            $"id={forward.Id}, vmPort={vmPort}, target={target}, publicPort={forward.PublicPort?.ToString() ?? "-"}");

        return TypedResults.Created(
            $"/api/v1/vms/{vm.Name}/forwards/{forward.Id}",
            ForwardResponse.From(forward, options.PublicHost));
    }

    private static async Task<IResult> DeleteAsync(
        string name,
        string id,
        HttpContext http,
        IVmRepository repository,
        IAuthorizationService authorization,
        IPortForwardManager forwards,
        CancellationToken cancellationToken)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, repository, authorization, name,
            Policies.VmSelfOrOwnerOrAdmin, cancellationToken).ConfigureAwait(false);

        if (!lookup.Ok)
        {
            return lookup.Failure!;
        }

        var vm = lookup.Vm!;
        http.SetAuditDetail($"id={id}");

        return await forwards.RemoveForwardAsync(vm.Name, id, cancellationToken).ConfigureAwait(false)
            ? TypedResults.NoContent()
            : Problems.NotFound($"VM '{vm.Name}' has no forward '{id}'.");
    }
}
