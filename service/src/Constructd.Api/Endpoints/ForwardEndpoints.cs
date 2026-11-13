using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Https;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
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
            .RequireAuthorization(Policies.VmScoped).WithName("ListForwards").Produces<IReadOnlyList<ForwardResponse>>();

        // Whether this VM may publish HTTPS forwards: `construct expose --help` only offers the option then.
        api.MapGet("/vms/{name}/https", HttpsAsync)
            .RequireAuthorization(Policies.VmScoped).WithName("VmHttpsAvailability").Produces<Constructd.Api.Https.HttpsAvailabilityResult>();

        api.MapPost("/vms/{name}/forwards", CreateAsync)
            .RequireAuthorization(Policies.VmScoped).Audited("forward.add").WithName("CreateForward").Produces<ForwardResponse>(StatusCodes.Status201Created).Produces<ForwardResponse>();

        api.MapDelete("/vms/{name}/forwards/{id}", DeleteAsync)
            .RequireAuthorization(Policies.VmScoped).Audited("forward.remove").WithName("DeleteForward").Produces(StatusCodes.Status204NoContent);

        // Policies.User, NOT Policies.VmScoped: this is the one forward route the VM's own token
        // may not reach. The guest ASKS for a client forward; whether a port really opened on the
        // user's PC is the extension's answer to give, and a VM that could write it would be able
        // to hand its own agents a link to a port nothing is listening on.
        api.MapPost("/vms/{name}/forwards/{id}/ack", AckAsync)
            .RequireAuthorization(Policies.User).Audited("forward.ack").WithName("AckForward").Produces<ForwardResponse>();

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
            Policies.ForwardRequester, cancellationToken).ConfigureAwait(false);

        if (!lookup.Ok)
        {
            return lookup.Failure!;
        }

        var vm = lookup.Vm!;
        IReadOnlyList<PortForward> list = await forwards.ListAsync(vm.Name, cancellationToken);
        var via = http.Request.Query["via"].FirstOrDefault();
        if (http.User.IsVmToken() && !http.User.IsPrimaryToken() && (via is not null || string.Equals(http.Request.Query["includeChildren"], "true", StringComparison.OrdinalIgnoreCase)))
            return Problems.Forbidden("Legacy VM tokens cannot access child forwards.");
        if (via is not null)
        {
            var primary = await repository.GetAsync(via, cancellationToken);
            if (primary is not { Kind: VmKind.Primary, Deleting: false } ||
                (http.User.IsVmToken() ? !string.Equals(http.User.VmTokenName(), primary.Name, StringComparison.OrdinalIgnoreCase)
                    : !string.Equals(http.User.NameOrEmpty(), primary.Owner, StringComparison.OrdinalIgnoreCase)))
                return Problems.Forbidden("Only the via primary or its owner may poll its tunnels.");
            list = list.Where(f => f.Destination is null).Concat(await http.RequestServices.GetRequiredService<IAccessExposure>().ListViaAsync(primary.Name, cancellationToken)).DistinctBy(f => f.Id).ToArray();
        }
        else if (vm.Kind == VmKind.Primary && string.Equals(http.Request.Query["includeChildren"], "true", StringComparison.OrdinalIgnoreCase))
        {
            var children = (await repository.ListAsync(null, cancellationToken)).Where(c => string.Equals(c.Parent, vm.Name, StringComparison.OrdinalIgnoreCase)).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            list = list.Concat((await forwards.ListAsync(null, cancellationToken)).Where(f => children.Contains(f.VmName))).ToArray();
        }
        else if (vm.Kind == VmKind.Child)
        {
            var relationship = await DelegationAuthorization.RelationshipAsync(http.User, vm, repository, http.RequestServices.GetRequiredService<IUserStore>(), cancellationToken);
            if (relationship == ForwardRelationship.Shared)
                list = list.Where(f => string.Equals(f.Destination?.RequestedBy, ForwardRequesterHandler.Requester(http.User), StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        // Advertised under THIS VM's public host (plan §4.12) — the service's PublicHost unless the
        // host runs a PublicHostPattern, in which case every VM has its own name.
        var publicHost = options.PublicHostFor(lookup.Vm!.Name);
        var mainPort = await HttpsMainPortAsync(http, options, cancellationToken);
        var routes = await HttpsRoutesAsync(http, options, mainPort, cancellationToken);
        return TypedResults.Ok(list.Select(f => ForwardResponse.From(f, publicHost, mainPort, SharedHost(http, routes, f))).ToList());
    }

    private static async Task<IResult> HttpsAsync(
        string name,
        HttpContext http,
        IVmRepository repository,
        IAuthorizationService authorization,
        CancellationToken cancellationToken)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, repository, authorization, name,
            Policies.ForwardRequester, cancellationToken).ConfigureAwait(false);
        return lookup.Ok
            ? TypedResults.Ok(await HttpsAvailability.CheckAsync(lookup.Vm!, http.RequestServices, cancellationToken))
            : lookup.Failure!;
    }

    private static async Task<IResult> CreateAsync(
        string name,
        CreateForwardRequest? request,
        HttpContext http,
        IVmRepository repository,
        IUserStore users,
        IHostConfigStore hostConfig,
        IAuthorizationService authorization,
        IPortForwardManager forwards,
        ConstructdOptions options,
        CancellationToken cancellationToken)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, repository, authorization, name,
            Policies.ForwardRequester, cancellationToken).ConfigureAwait(false);

        if (!lookup.Ok)
        {
            return lookup.Failure!;
        }

        var vm = lookup.Vm!;
        var https = request?.Https == true;
        if ((request?.Main == true || request?.UpstreamTls is not null || request?.Name is not null || request?.ReplaceMain is not null) && !https)
            return CodedProblems.Validation("https", "'main', 'upstreamTls', 'name' and 'replaceMain' need https=true.");
        if (request?.ReplaceMain == true && request.Main != true)
            return CodedProblems.Validation("replaceMain", "'replaceMain' needs main=true.");
        if (https && vm.Kind == VmKind.Child)
            return CodedProblems.Create(400, "https-primary-only", "HTTPS forwards are for primary VMs.");
        if (vm.Kind == VmKind.Child) return await ChildForwardOperations.CreateAsync(vm, request, http, cancellationToken);
        if (request?.ConnectPort is not null || request?.Via is not null)
            return CodedProblems.Validation("destination", "Primary forwards cannot specify via or connectPort.");

        if (ApiHelpers.FenceDeleting(vm) is { } fenced)
        {
            http.SetAuditDetail("vm is being deleted");
            return fenced;
        }

        if (request?.VmPort is not int vmPort || vmPort is < 1 or > 65535)
        {
            return Problems.BadRequest("'vmPort' must be between 1 and 65535.");
        }

        // Client is the default target: private to the user's PC, no LAN exposure (plan §4.6). An HTTPS
        // forward is served by the host, so it is a host forward whether or not the target was named.
        var target = https ? ForwardTarget.Host : ForwardTarget.Client;
        if (!string.IsNullOrWhiteSpace(request.Target) &&
            !ApiHelpers.TryParseEnum(request.Target, out target))
        {
            return Problems.BadRequest($"'target' must be one of: {ApiHelpers.Options<ForwardTarget>()}.");
        }

        if (https && target != ForwardTarget.Host)
        {
            return CodedProblems.Validation("target", "An HTTPS forward is served by the host: target must be host.");
        }

        if (https && await HttpsAvailability.CheckAsync(vm, http.RequestServices, cancellationToken) is { Available: false } unavailable)
        {
            http.SetAuditDetail($"https unavailable: {unavailable.Reason}");
            return CodedProblems.Create(unavailable.Reason == "not-allowed" ? 403 : 409, "https-unavailable", unavailable.Message!);
        }

        var label = request.Label?.Trim() ?? string.Empty;
        var network = http.RequestServices.GetRequiredService<VmNetworkSettings>();
        await using var networkGate = network.Supported
            ? await PrimaryOperationGate.AcquireAsync(http.RequestServices.GetRequiredService<IVmOperationGate>(), name, http.TraceIdentifier, cancellationToken) : null;
        if (network.Supported && networkGate is null) return LifecycleEndpoints.Busy(vm.CurrentJobId);
        vm = (await repository.GetAsync(name, cancellationToken))!;
        if (vm is null || vm.Deleting) return Problems.Conflict("VM is gone or being deleted.");

        if (target == ForwardTarget.Host)
        {
            if (!https && (await network.CurrentAsync(vm, cancellationToken)).Mode == "direct")
            {
                var endpoint = await network.EndpointAsync(vm, cancellationToken);
                if (endpoint is null) return CodedProblems.Create(409, "no-address", "The guest has not reported an address yet.");
                http.SetAuditDetail($"kind=direct, vmPort={vmPort}");
                return Results.Ok(new ForwardResponse("", vm.Name, vmPort, null, target, label,
                    http.RequestServices.GetRequiredService<IClock>().UtcNow,
                    $"http://{ForwardHost.ForUrl(endpoint.SshHost)}:{vmPort}/", Kind: "direct"));
            }
            if ((await hostConfig.GetAsync<NetworkConfig>("network", cancellationToken)) is { HostForwardsEnabled: false })
                return CodedProblems.Create(403, "host-forwards-disabled", "Host-target forwards are disabled on this host.");
            // The policy follows the VM's OWNER, not the caller: an admin acting on someone else's VM
            // must not be able to route around that user's restriction.
            // An HTTPS forward has its own permission, checked above (HttpsAvailability).
            var owner = await users.GetAsync(vm.Owner, cancellationToken).ConfigureAwait(false);
            if (!https && (owner is null || !owner.AllowHostForwards))
            {
                http.SetAuditDetail("host forwards disabled for the owner");
                return Problems.Forbidden(
                    $"Host-target forwards are disabled for '{vm.Owner}'. Use target=client.");
            }
        }

        string? httpsName = null;
        if (https)
        {
            // docs/https-forwards.md, "Forward names": one name per HTTPS forward across the host, never a VM's or
            // the vault's, and one open main forward per VM unless the request takes it over.
            // Only an EXPLICIT name is checked. The default one (the VM port) never refuses a forward: when it is
            // too long or taken, the route table just leaves it out and the forward keeps its port address.
            if (request.Name is not null)
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                    return CodedProblems.Validation("name", $"An empty name is not a forward name: use {HttpsForwardNames.Rule}, or leave it out.");
                httpsName = request.Name.Trim().ToLowerInvariant();
                if (HttpsForwardNames.Problem(vm.Name, httpsName) is { } problem)
                    return CodedProblems.Validation("name", problem);
                if (await HttpsNameConflictAsync(http, options, vm.Name, vmPort, httpsName, cancellationToken) is { } conflict)
                {
                    http.SetAuditDetail($"https name {HttpsForwardNames.Label(vm.Name, httpsName)} taken");
                    return CodedProblems.Create(409, "https-name-taken", conflict);
                }
            }

            if (request.Main == true && request.ReplaceMain != true &&
                (await http.RequestServices.GetRequiredService<IForwardStore>().ListAsync(vm.Name, cancellationToken))
                    .Where(f => f.Https && f.Main && f.PublicPort is not null)
                    .OrderByDescending(f => f.Created).FirstOrDefault() is { } holder)
            {
                http.SetAuditDetail($"main held by {holder.Id}");
                return CodedProblems.Create(409, "main-taken",
                    $"'{(holder.Label.Length > 0 ? holder.Label : "port " + holder.VmPort)}' (VM port {holder.VmPort}) is this VM's main HTTPS forward. " +
                    "Close it, or ask to replace it (construct expose --replace-main, replaceMain=true).");
            }
        }

        AddForwardResult result;
        try
        {
            // The cap, the "is this VM still there" check and the insert all happen inside the manager
            // under the VM's own gate: a guest could otherwise fire concurrent requests through the gap
            // between counting and adding, or attach a forward to a VM that is being torn down.
            result = https
                ? await forwards.TryAddHttpsForwardAsync(vm.Name, vmPort, label,
                    new HttpsForwardOptions(request.Main == true, request.UpstreamTls, httpsName), options.MaxForwardsPerVm, cancellationToken)
                    .ConfigureAwait(false)
                : await forwards.TryAddForwardAsync(
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
            $"id={forward.Id}, vmPort={vmPort}, target={target}, publicPort={forward.PublicPort?.ToString() ?? "-"}" +
            (forward.Https ? $", https, main={forward.Main}, name={HttpsForwardNames.Effective(forward)}" : ""));

        var sharedPort = await HttpsMainPortAsync(http, options, cancellationToken);
        var routes = await HttpsRoutesAsync(http, options, sharedPort, cancellationToken);
        return TypedResults.Created(
            $"/api/v1/vms/{vm.Name}/forwards/{forward.Id}",
            ForwardResponse.From(forward, options.PublicHostFor(vm.Name), sharedPort, SharedHost(http, routes, forward)));
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
            Policies.ForwardRequester, cancellationToken).ConfigureAwait(false);

        if (!lookup.Ok)
        {
            return lookup.Failure!;
        }

        var vm = lookup.Vm!;
        http.SetAuditDetail($"id={id}");
        if (vm.Kind == VmKind.Child)
        {
            var forward = await http.RequestServices.GetRequiredService<IForwardStore>().GetAsync(id, cancellationToken);
            if (forward is null || !string.Equals(forward.VmName, vm.Name, StringComparison.OrdinalIgnoreCase)) return Problems.NotFound("Unknown forward.");
            if (!await ChildForwardOperations.MayRemoveAsync(http, vm, forward, cancellationToken)) return Problems.Forbidden("Only the owner, parent, administrator or requester may remove this forward.");
        }

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
        var candidate = await repository.GetAsync(name, cancellationToken);
        Vm vm;
        if (candidate?.Kind == VmKind.Child)
        {
            var childForward = await store.GetAsync(id, cancellationToken);
            if (childForward is null || !string.Equals(childForward.VmName, candidate.Name, StringComparison.OrdinalIgnoreCase)) return Problems.NotFound("Unknown forward.");
            if (!await ChildForwardOperations.MayAckAsync(http, candidate, childForward, cancellationToken)) return Problems.Forbidden("Only the via primary's owner or an administrator may ack.");
            vm = candidate;
        }
        else
        {
            var lookup = await ApiHelpers.ResolveVmAsync(http, repository, authorization, name, Policies.VmOwnerOrAdmin, cancellationToken);
            if (!lookup.Ok) return lookup.Failure!;
            vm = lookup.Vm!;
        }
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

        if (vm.Kind == VmKind.Child && status == AckStatus.Open && forward.Destination?.ConnectAddress is null)
            return CodedProblems.Create(409, "address-unverifiable", "The guest address is not usable yet.");
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

        if (vm.Kind == VmKind.Child && !await http.RequestServices.GetRequiredService<Constructd.Core.Services.AccessExposure>().TryAckAsync(forward, ack, cancellationToken))
            return CodedProblems.Create(409, "address-unverifiable", "The child destination changed; poll its forwards again.");
        if (vm.Kind != VmKind.Child && !await store.SetAckAsync(id, ack, cancellationToken).ConfigureAwait(false))
        {
            // Removed between the read and the write (the guest ran `expose --close`).
            return Problems.NotFound($"VM '{vm.Name}' has no forward '{id}'.");
        }

        http.SetAuditDetail(
            $"id={id}, status={ApiHelpers.Name(status)}, localPort={localPort?.ToString() ?? "-"}" +
            $", hostLabel={(string.IsNullOrEmpty(hostLabel) ? "-" : hostLabel)}");

        return TypedResults.Ok(ForwardResponse.From(forward with { Ack = ack }, options.PublicHostFor(vm.Name)));
    }

    /// <summary>
    /// The shared HTTPS port when main HTTPS forwards are reachable on it under each VM's own name: a shared
    /// port is configured and <c>PublicHostPattern</c> gives every VM a name of its own (docs/https-forwards.md).
    /// </summary>
    internal static async Task<int?> HttpsMainPortAsync(HttpContext http, ConstructdOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.PublicHostPattern)) return null;
        var gateway = http.RequestServices.GetService<HttpsGateway>();
        return gateway is null ? null : await gateway.ServingSharedPortAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The shared port's route table when HTTPS forwards are served there by name, null otherwise.</summary>
    internal static async Task<HttpsRouteTable?> HttpsRoutesAsync(HttpContext http, ConstructdOptions options, int? sharedPort,
        CancellationToken cancellationToken)
    {
        if (sharedPort is null || string.IsNullOrWhiteSpace(options.PublicHostPattern)) return null;
        var services = http.RequestServices;
        return await HttpsGateway.BuildRoutesAsync(services.GetRequiredService<IForwardStore>(), services.GetRequiredService<IVmRepository>(),
            services.GetRequiredService<IHostConfigStore>(), options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The name the shared port serves <paramref name="forward"/> under, when the host's certificate covers it; null
    /// keeps the forward's own port address.
    /// </summary>
    internal static string? SharedHost(HttpContext http, HttpsRouteTable? routes, PortForward forward)
    {
        if (routes is null || !forward.Https || routes.HostOf(forward) is not { } host) return null;
        return http.RequestServices.GetService<HttpsCertificateStore>()?.Current is { } certificate && certificate.Covers(host) ? host : null;
    }

    /// <summary>
    /// Why <c>&lt;vm&gt;-&lt;name&gt;</c> cannot be a new HTTPS forward's name, or null: it is a VM's name, the vault's
    /// web host, or another open HTTPS forward's name. Compared as labels, so it holds whether or not the host runs
    /// a per-VM name pattern yet.
    /// </summary>
    private static async Task<string?> HttpsNameConflictAsync(HttpContext http, ConstructdOptions options, string vmName, int vmPort, string name,
        CancellationToken cancellationToken)
    {
        var services = http.RequestServices;
        var label = HttpsForwardNames.Label(vmName, name);
        if ((await services.GetRequiredService<IVmRepository>().ListAsync(owner: null, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(v => string.Equals(v.Name, label, StringComparison.OrdinalIgnoreCase)) is { } vm)
            return $"'{label}' is the name of the VM '{vm.Name}'. Choose another name (construct expose --name).";

        var routes = await HttpsGateway.BuildRoutesAsync(services.GetRequiredService<IForwardStore>(), services.GetRequiredService<IVmRepository>(),
            services.GetRequiredService<IHostConfigStore>(), options, cancellationToken).ConfigureAwait(false);
        var host = HttpsForwardNames.Host(options.PublicHostPattern, options.PublicHost, vmName, name);
        if (host is not null && string.Equals(host, routes.VaultHost, StringComparison.OrdinalIgnoreCase))
            return $"'{host}' is the key vault's address. Choose another name (construct expose --name).";

        var taken = (await services.GetRequiredService<IForwardStore>().ListAsync(vmName: null, cancellationToken).ConfigureAwait(false))
            .Where(f => f.Https && f.PublicPort is not null)
            .FirstOrDefault(f => string.Equals(HttpsForwardNames.Label(f.VmName, HttpsForwardNames.Effective(f)), label, StringComparison.OrdinalIgnoreCase));
        return taken is null ? null
            : string.Equals(taken.VmName, vmName, StringComparison.OrdinalIgnoreCase)
                ? $"This VM already has an HTTPS forward named '{name}' ('{(taken.Label.Length > 0 ? taken.Label : "port " + taken.VmPort)}', VM port {taken.VmPort}). Choose another name (construct expose --name)" +
                  (taken.VmPort == vmPort ? ", or reuse it (--reuse)." : ".")
                : $"'{label}' is already the name of an HTTPS forward of the VM '{taken.VmName}'. Choose another name (construct expose --name).";
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
