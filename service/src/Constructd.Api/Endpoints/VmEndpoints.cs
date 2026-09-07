using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Microsoft.AspNetCore.Authorization;

namespace Constructd.Api.Endpoints;

/// <summary>VM lifecycle: list, create (job), read, delete (job), power, state, endpoint.</summary>
public static class VmEndpoints
{
    private const int MinCpu = 1;
    private const int MaxCpu = 64;
    private const int MinRamGb = 1;
    private const int MaxRamGb = 1024;
    private const int MinDiskGb = 8;
    private const int MaxDiskGb = 8192;

    public static RouteGroupBuilder MapVmEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/vms", ListAsync)
            .RequireAuthorization(Policies.UserOrPrimaryToken).WithName("ListVms");

        api.MapPost("/vms", CreateAsync)
            .RequireAuthorization(Policies.User).Audited("vm.create").WithName("CreateVm");

        api.MapGet("/vms/{name}", GetAsync)
            .RequireAuthorization(Policies.UserOrPrimaryToken).WithName("GetVm");

        api.MapDelete("/vms/{name}", DeleteAsync)
            .RequireAuthorization(Policies.UserOrPrimaryToken).Audited("vm.delete").WithName("DeleteVm");

        api.MapPost("/vms/{name}/power", PowerAsync)
            .RequireAuthorization(Policies.User).Audited("vm.power").WithName("PowerVm");

        api.MapGet("/vms/{name}/state", StateAsync)
            .RequireAuthorization(Policies.UserOrPrimaryToken).WithName("GetVmState");

        api.MapGet("/vms/{name}/endpoint", EndpointAsync)
            .RequireAuthorization(Policies.UserOrPrimaryToken).WithName("GetVmEndpoint");

        return api;
    }

    private static async Task<IResult> ListAsync(HttpContext http, IVmRepository repository, VmInventoryProjection projection, CancellationToken cancellationToken)
    {
        var kind = http.Request.Query["kind"].ToString(); var ownerFilter = http.Request.Query["owner"].ToString(); var parent = http.Request.Query["parent"].ToString();
        if (kind is not ("" or "all" or "primary" or "child")) return CodedProblems.Validation("kind", "Expected primary, child or all.");
        if (ownerFilter.Length > 0 && !http.User.IsAdmin()) return Problems.Forbidden("Only admins may filter by owner.");
        var owner = http.User.IsAdmin() ? (ownerFilter.Length > 0 ? ownerFilter : null) : http.User.IsVmToken() ? null : http.User.NameOrEmpty();
        var all = await repository.ListAsync(owner, cancellationToken); var result = new List<VmResponse>();
        foreach (var vm in all)
        {
            if (http.User.IsVmToken() && !Ownership.SameName(vm.Name, http.User.VmTokenName()) && !Ownership.SameName(vm.Parent, http.User.VmTokenName())) continue;
            if (kind == "primary" && vm.Kind != VmKind.Primary || kind == "child" && vm.Kind != VmKind.Child || parent.Length > 0 && !Ownership.SameName(vm.Parent, parent)) continue;
            result.Add(await projection.ProjectAsync(vm, http.User, cancellationToken));
        }
        return Results.Ok(result);
    }

    private static async Task<IResult> CreateAsync(
        CreateVmRequest? request,
        HttpContext http,
        IVmRepository repository,
        IUserStore users,
        IJobEngine jobs,
        IClock clock,
        ConstructdOptions options,
        IServiceScopeFactory scopes,
        CancellationToken cancellationToken)
    {
        var actor = http.User.NameOrEmpty();
        var name = request?.Name?.Trim() ?? string.Empty;

        if (!VmNameValidator.IsValid(name))
        {
            return Problems.BadRequest($"'name' is invalid: {VmNameValidator.Rule}");
        }

        http.SetAuditTarget(name);

        if (request!.Cpu is not int cpu || cpu is < MinCpu or > MaxCpu)
        {
            return Problems.BadRequest($"'cpu' must be between {MinCpu} and {MaxCpu}.");
        }

        if (request.RamGb is not int ramGb || ramGb is < MinRamGb or > MaxRamGb)
        {
            return Problems.BadRequest($"'ramGb' must be between {MinRamGb} and {MaxRamGb}.");
        }

        if (request.DiskGb is not int diskGb || diskGb is < MinDiskGb or > MaxDiskGb)
        {
            return Problems.BadRequest($"'diskGb' must be between {MinDiskGb} and {MaxDiskGb}.");
        }

        var policy = IdlePolicyRules.Default(options.Idle);
        if (request.Opts?.IdlePolicy is { } requested)
        {
            if (!TryReadPolicy(requested, options, out policy, out var policyError))
            {
                return policyError;
            }
        }

        var user = await users.GetAsync(actor, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Problems.Forbidden("You are not enrolled on this host.");
        }

        var vm = new Vm(
            Name: name,
            Owner: actor,
            Cpu: cpu,
            RamGb: ramGb,
            DiskGb: diskGb,
            Created: clock.UtcNow,
            State: VmState.Unknown,
            SshForwardPort: null,
            VmTokenHash: null,
            IdlePolicy: policy,
            Forwards: Vm.NoForwards);

        var descriptor = new VmDescriptor(name, cpu, ramGb, diskGb, IsoPath: null,
            Nested: request.Opts?.Nested ?? false, AutomaticCheckpoints: request.Opts?.AutomaticCheckpoints ?? false);
        return await PrimaryVmAdmission.CreateAsync(vm, descriptor, request, http, cancellationToken);
    }

    private static async Task<IResult> GetAsync(
        string name,
        HttpContext http,
        IVmRepository repository,
        IAuthorizationService authorization,
        VmInventoryProjection projection,
        CancellationToken cancellationToken)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, repository, authorization, name,
            Policies.ChildOperator, cancellationToken).ConfigureAwait(false);

        return lookup.Ok
            ? TypedResults.Ok(await projection.ProjectAsync(lookup.Vm!, http.User, cancellationToken)
                .ConfigureAwait(false))
            : lookup.Failure!;
    }

    private static async Task<IResult> DeleteAsync(
        string name,
        HttpContext http,
        IVmRepository repository,
        IAuthorizationService authorization,
        IJobEngine jobs,
        IVmOperationGate gate,
        IVmMetadataStore metadata,
        IServiceScopeFactory scopes,
        CancellationToken cancellationToken)
    {
        var initial = await repository.GetAsync(name, cancellationToken);
        if (initial is null) return Problems.NotFound("Unknown VM.");
        var policy = initial.Kind == VmKind.Child ? Policies.ChildOwnerOrAdmin : Policies.VmOwnerOrAdmin;
        if (!(await authorization.AuthorizeAsync(http.User, initial, policy)).Succeeded) return LifecycleEndpoints.Problem("not-owner", 403);
        await using var handle = await PrimaryOperationGate.AcquireAsync(gate, name, http.TraceIdentifier, cancellationToken);
        if (handle is null)
        {
            var latest = await repository.GetAsync(name, cancellationToken);
            if (latest is { Deleting: true } && await LifecycleEndpoints.LiveAsync(latest, http.RequestServices, cancellationToken))
                return Results.Ok(new { jobId = latest.CurrentJobId, replayed = true });
            gate.IsHeld(name, out var currentOperation); return LifecycleEndpoints.Busy(currentOperation);
        }
        var vm = await repository.GetAsync(name, cancellationToken);
        if (vm is null) return Problems.NotFound("Unknown VM.");
        if (!(await authorization.AuthorizeAsync(http.User, vm, policy)).Succeeded) return LifecycleEndpoints.Problem("not-owner", 403);
        return vm.Kind == VmKind.Child ? await ChildVmEndpoints.DeleteAsync(vm, http, cancellationToken) :
            await CascadeEndpoints.DeleteAsync(vm, http, cancellationToken);
    }

    private static async Task<IResult> PowerAsync(
        string name,
        PowerRequest? request,
        HttpContext http,
        IVmRepository repository,
        IAuthorizationService authorization,
        IHypervisorDriver driver,
        CancellationToken cancellationToken)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, repository, authorization, name,
            Policies.VmOwnerOrAdmin, cancellationToken).ConfigureAwait(false);

        if (!lookup.Ok)
        {
            return lookup.Failure!;
        }

        var vm = lookup.Vm!;
        if (vm.Kind == VmKind.Child) return CodedProblems.Create(400, "child-lifecycle-route", "Use the child lifecycle route.");

        if (ApiHelpers.FenceDeleting(vm) is { } fenced)
        {
            http.SetAuditDetail("vm is being deleted");
            return fenced;
        }

        var action = request?.Action?.Trim().ToLowerInvariant();

        if (action is not ("start" or "stop" or "save"))
        {
            return Problems.BadRequest("'action' must be one of: start|stop|save.");
        }

        http.SetAuditDetail($"action={action}");

        if (action == "save" && !driver.Capabilities.Suspend)
        {
            return Problems.Conflict("This host's driver cannot suspend VMs.");
        }

        var services = http.RequestServices;
        var vmGate = services.GetRequiredService<IVmOperationGate>();
        await using var held = await PrimaryOperationGate.AcquireAsync(vmGate, vm.Name, http.TraceIdentifier, cancellationToken);
        if (held is null) { vmGate.IsHeld(vm.Name, out var operation); return LifecycleEndpoints.Busy(operation); }
        vm = (await repository.GetAsync(vm.Name, cancellationToken))!;
        if (vm is null) return Problems.NotFound("Unknown VM.");
        if (ApiHelpers.FenceDeleting(vm) is { } latestFence) return latestFence;
        if (await LifecycleEndpoints.LiveAsync(vm, services, cancellationToken)) return LifecycleEndpoints.Busy(vm.CurrentJobId);
        VmState state;
        if (action == "start")
        {
            // Keep the legacy idempotent Running answer; Off/Saved/Paused starts use the shared ledger.
            state = await driver.GetStateAsync(vm.Name, cancellationToken);
            if (state != VmState.Running)
            {
                var supplied = http.Request.Headers["X-Construct-Operation-Key"].FirstOrDefault();
                if (supplied is not null && !OperationFingerprint.ValidKey(supplied)) return CodedProblems.Validation("operationKey", "Invalid operation key.");
                var key = new OperationKeyRecord(vm.Owner, "lifecycle-start", supplied ?? Guid.NewGuid().ToString("n"), "primary-power-start:" + vm.Name,
                    vm.Name, null, OperationKeyState.InFlight, null, vm.PowerGeneration, null, services.GetRequiredService<IClock>().UtcNow);
                try
                {
                    var reply = await services.GetRequiredService<LifecycleStart>().RunAsync(vm, null, null, key, cancellationToken);
                    if (reply.Code is not null) return LifecycleEndpoints.Problem(reply.Code);
                    state = reply.State;
                }
                catch (LifecycleException ex) { return ex.Capacity is { } decision ? PrimaryVmAdmission.CapacityProblem(decision) : LifecycleEndpoints.Problem(ex.Code); }
            }
        }
        else
        {
            if (action == "stop") await driver.StopAsync(vm.Name, cancellationToken);
            else await driver.SaveAsync(vm.Name, cancellationToken);
            state = await driver.GetStateAsync(vm.Name, cancellationToken);
            try { await services.GetRequiredService<ChildLifecycleJobs>().PersistState(vm, state, null, true, cancellationToken); }
            catch (LifecycleException ex) { return LifecycleEndpoints.Problem(ex.Code); }
        }

        http.SetAuditDetail($"action={action}, state={state}");
        return TypedResults.Ok(new VmStateResponse(state));
    }

    private static async Task<IResult> StateAsync(
        string name,
        HttpContext http,
        IVmRepository repository,
        IAuthorizationService authorization,
        IHypervisorDriver driver,
        CancellationToken cancellationToken)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, repository, authorization, name,
            Policies.ChildOperator, cancellationToken).ConfigureAwait(false);

        if (!lookup.Ok)
        {
            return lookup.Failure!;
        }

        var vm = lookup.Vm!;
        var state = await driver.GetStateAsync(vm.Name, cancellationToken).ConfigureAwait(false);

        // This is a read. Reconciliation persists observed transitions under the VM gate,
        // preserving pending start intents and their power-generation fence.
        return TypedResults.Ok(new VmStateResponse(state));
    }

    /// <remarks>
    /// The endpoint of a remote VM is defined as the service host plus its allocated forward
    /// (plan §4.2/§4.4). Until that forward exists there is no dialable address — the VM sits on an
    /// internal NAT switch the client cannot reach — so the call reports "not ready yet" instead of
    /// handing out an address that cannot work.
    /// </remarks>
    private static async Task<IResult> EndpointAsync(
        string name,
        HttpContext http,
        IVmRepository repository,
        IAuthorizationService authorization,
        ConstructdOptions options,
        CancellationToken cancellationToken)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, repository, authorization, name,
            Policies.ChildOperator, cancellationToken).ConfigureAwait(false);

        if (!lookup.Ok)
        {
            return lookup.Failure!;
        }

        var vm = lookup.Vm!;
        if (vm.Kind == VmKind.Child) return CodedProblems.Create(409, "no-endpoint", "Children have no primary SSH endpoint.");
        // sshHost is where SSH is dialled (the service host plus the allocated forward); publicHost is
        // the name this VM's WEB forwards are advertised under (plan §4.12). Without a
        // PublicHostPattern the two are the same string, which is what makes this addition invisible
        // to an existing host.
        return vm.SshForwardPort is int port
            ? TypedResults.Ok(new EndpointResponse(options.PublicHost, port, options.PublicHostFor(vm.Name)))
            : Problems.UnavailableYet(
                $"VM '{vm.Name}' has no ssh forward yet; wait for its creation job to finish.");
    }

    /// <summary>Parses and clamps an idle policy from a request body.</summary>
    internal static bool TryReadPolicy(
        IdlePolicyRequest request,
        ConstructdOptions options,
        out IdlePolicy policy,
        out IResult error)
    {
        policy = IdlePolicy.Disabled;
        error = Problems.BadRequest("invalid idle policy");

        if (request.TimeoutMinutes is not int timeout || timeout < 0)
        {
            error = Problems.BadRequest("'timeoutMinutes' must be 0 (off) or a positive number.");
            return false;
        }

        if (!ApiHelpers.TryParseEnum<IdleAction>(request.Action, out var action))
        {
            error = Problems.BadRequest($"'action' must be one of: {ApiHelpers.Options<IdleAction>()}.");
            return false;
        }

        policy = IdlePolicyRules.Clamp(new IdlePolicy(timeout, action), options.Idle);
        return true;
    }
}
