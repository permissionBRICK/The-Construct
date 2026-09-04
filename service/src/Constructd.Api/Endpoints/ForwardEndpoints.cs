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

        // Policies.User, NOT Policies.VmScoped: this is the one forward route the VM's own token
        // may not reach. The guest ASKS for a client forward; whether a port really opened on the
        // user's PC is the extension's answer to give, and a VM that could write it would be able
        // to hand its own agents a link to a port nothing is listening on.
        api.MapPost("/vms/{name}/forwards/{id}/ack", AckAsync)
            .RequireAuthorization(Policies.User).Audited("forward.ack").WithName("AckForward");

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
        // Advertised under THIS VM's public host (plan §4.12) — the service's PublicHost unless the
        // host runs a PublicHostPattern, in which case every VM has its own name.
        var publicHost = options.PublicHostFor(lookup.Vm!.Name);
        return TypedResults.Ok(list.Select(f => ForwardResponse.From(f, publicHost)).ToList());
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
            ForwardResponse.From(forward, options.PublicHostFor(vm.Name)));
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

    /// <summary>
    /// The client-forward ack relay (plan §4.6): the owner's extension reports that it opened the
    /// port on the user's PC — or that it could not — and <c>construct expose</c>, polling the list
    /// with the VM token, finally has a link to print.
    ///
    /// The store is written directly rather than through <see cref="IPortForwardManager"/>: an ack
    /// changes nothing on the host (no rule, no port allocation), so there is nothing for the
    /// platform implementation to do and no reason for a Windows-only type to be in this path.
    /// </summary>
    private static async Task<IResult> AckAsync(
        string name,
        string id,
        ForwardAckRequest? request,
        HttpContext http,
        IVmRepository repository,
        IAuthorizationService authorization,
        IForwardStore store,
        IClock clock,
        ConstructdOptions options,
        CancellationToken cancellationToken)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, repository, authorization, name,
            Policies.VmOwnerOrAdmin, cancellationToken).ConfigureAwait(false);

        if (!lookup.Ok)
        {
            return lookup.Failure!;
        }

        var vm = lookup.Vm!;
        http.SetAuditDetail($"id={id}");

        if (ApiHelpers.FenceDeleting(vm) is { } fenced)
        {
            http.SetAuditDetail($"id={id}, vm is being deleted");
            return fenced;
        }

        var forward = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);

        // A forward of somebody else's VM is a 404 here, not a 403: the caller is already
        // authorized for THIS VM, so the only thing being reported is that this VM has no such
        // forward — which is exactly what "the id is another VM's" means to them.
        if (forward is null || !string.Equals(forward.VmName, vm.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Problems.NotFound($"VM '{vm.Name}' has no forward '{id}'.");
        }

        if (forward.Target != ForwardTarget.Client)
        {
            http.SetAuditDetail($"id={id}, target={forward.Target}");
            return Problems.Conflict(
                $"Forward '{id}' has target={ApiHelpers.Name(forward.Target)}: the service materializes it " +
                "itself, so there is no client ack to record.");
        }

        if (request is null)
        {
            return Problems.BadRequest("A body with 'status' is required.");
        }

        if (!ApiHelpers.TryParseEnum<AckStatus>(request.Status, out var status))
        {
            return Problems.BadRequest($"'status' must be one of: {ApiHelpers.Options<AckStatus>()}.");
        }

        int? localPort = null;
        if (status == AckStatus.Open)
        {
            if (request.LocalPort is not int port || port is < 1 or > 65535)
            {
                return Problems.BadRequest("'localPort' must be between 1 and 65535 for status=open.");
            }

            localPort = port;
        }
        else if (request.LocalPort is int reported)
        {
            // An error ack may still name the port it tried; a nonsense one is refused rather than
            // stored, because the same field builds the link when the status later flips to open.
            if (reported is < 1 or > 65535)
            {
                return Problems.BadRequest("'localPort' must be between 1 and 65535.");
            }

            localPort = reported;
        }

        // Stored in the ONE canonical wire form (ForwardHost, docs/expose.md): an IPv6 literal is
        // kept bare and bracketed only when a URL is built, so what this service echoes and what
        // the guest CLI prints cannot disagree about the same address.
        var hostLabel = ForwardHost.Normalize(Sanitize(request.HostLabel, MaxHostLabel));
        var message = Sanitize(request.Message, MaxAckMessage) ?? string.Empty;

        var ack = new ForwardAck(status, localPort, hostLabel, message, clock.UtcNow);

        if (!await store.SetAckAsync(id, ack, cancellationToken).ConfigureAwait(false))
        {
            // Removed between the read and the write (the guest ran `expose --close`).
            return Problems.NotFound($"VM '{vm.Name}' has no forward '{id}'.");
        }

        http.SetAuditDetail(
            $"id={id}, status={ApiHelpers.Name(status)}, localPort={localPort?.ToString() ?? "-"}" +
            $", hostLabel={(string.IsNullOrEmpty(hostLabel) ? "-" : hostLabel)}");

        return TypedResults.Ok(ForwardResponse.From(forward with { Ack = ack }, options.PublicHostFor(vm.Name)));
    }

    /// <summary>A host label ends up in a URL the CLI prints; a message ends up on its stderr.</summary>
    private const int MaxHostLabel = 200;

    private const int MaxAckMessage = 300;

    /// <summary>
    /// Trims, drops control characters and caps the length. The extension is trusted to be honest,
    /// not to be well-behaved: these two strings are echoed to a guest CLI that prints them, so a
    /// newline in either would let one field forge another line of its output.
    /// </summary>
    private static string? Sanitize(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = new string([.. value.Where(c => !char.IsControl(c))]).Trim();
        if (cleaned.Length == 0)
        {
            return null;
        }

        return cleaned.Length > max ? cleaned[..max] : cleaned;
    }
}
