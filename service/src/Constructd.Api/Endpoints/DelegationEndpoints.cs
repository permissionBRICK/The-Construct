using System.Text.RegularExpressions;
using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Microsoft.AspNetCore.Authorization;
namespace Constructd.Api.Endpoints;

public static class DelegationEndpoints
{
    public static RouteGroupBuilder MapDelegationEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/vms/{name}/identity", IdentityAsync).RequireAuthorization(Policies.VmScoped).WithName("VmIdentity");
        api.MapPost("/vms/{name}/guest-report", ReportAsync).RequireAuthorization(Policies.VmScoped).Audited("vm.guest-report").WithName("VmGuestReport");
        api.MapPost("/vms/{name}/token", RotateAsync).RequireAuthorization(Policies.User).Audited("vm.token.rotate").WithName("RotateVmToken");
        api.MapDelete("/vms/{name}/token", RevokeAsync).RequireAuthorization(Policies.User).Audited("vm.token.revoke").WithName("RevokeVmToken");
        api.MapGet("/vms/{name}/overrides", OverridesAsync).RequireAuthorization(Policies.Admin).WithName("GetVmOverrides");
        api.MapPut("/vms/{name}/overrides", SetOverridesAsync).RequireAuthorization(Policies.Admin).Audited("vm.overrides").WithName("SetVmOverrides");
        api.MapDelete("/vms/{name}/overrides", RemoveOverridesAsync).RequireAuthorization(Policies.Admin).Audited("vm.overrides").WithName("RemoveVmOverrides");
        api.MapGet("/vms/{name}/children", ChildrenAsync).RequireAuthorization(Policies.UserOrPrimaryToken).WithName("ListChildren");
        api.MapGet("/vms/shared", SharedAsync).RequireAuthorization(Policies.UserOrPrimaryToken).WithName("ListSharedVms");
        api.MapGet("/vms/{name}/capabilities", VmCapabilitiesAsync).RequireAuthorization(Policies.UserOrPrimaryToken).WithName("VmCapabilities");
        return api;
    }
    private static async Task<IResult> IdentityAsync(string name, HttpContext http, IVmRepository vms, IAuthorizationService authorization,
        IDelegationPolicy policy, IHostConfigStore config, IReleaseInfo release, ConstructdOptions options, CancellationToken ct)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, vms, authorization, name, Policies.VmSelfOrOwnerOrAdmin, ct);
        if (!lookup.Ok) return lookup.Failure!; var vm = lookup.Vm!;
        var legacy = http.User.IsVmToken() && !http.User.IsPrimaryToken();
        return Results.Ok(new
        {
            vmName = vm.Name,
            vm.Kind,
            tokenKind = http.User.IsVmToken() ? (VmTokenKind?)vm.TokenKind : null,
            vm.Owner,
            vm.Parent,
            delegation = legacy || vm.Kind != VmKind.Primary ? null : await HostAdminEndpoints.EffectiveAsync(vm.Owner, vm.Name, policy, ct),
            serviceCommit = release.Installed.Commit,
            apiFeatures = release.ApiFeatures,
            capacityMode = (await HostAdminEndpoints.CapacityConfigAsync(config, options, ct)).Mode
        });
    }
    private static async Task<IResult> ReportAsync(string name, GuestReportRequest request, HttpContext http, IVmRepository vms,
        IVmDelegationRepository reports, IAuthorizationService authorization, IClock clock, CancellationToken ct)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, vms, authorization, name, Policies.VmSelfOrOwnerOrAdmin, ct);
        if (!lookup.Ok) return lookup.Failure!; var vm = lookup.Vm!;
        if (vm.Deleting) return CodedProblems.Create(409, "vm-deleting", "VM is being deleted.");
        if (vm.Kind != VmKind.Primary) return CodedProblems.Create(409, "not-a-primary", "Only a primary can report Construct provisioning.");
        if (request.Reporter is not ("provision.sh" or "Provision-AgentVM.ps1")) return CodedProblems.Validation("reporter", "Expected the Construct provisioner.");
        if (request.Event is not ("provisioned" or "reinstalled" or "attempt")) return CodedProblems.Validation("event", "Expected provisioned, reinstalled or attempt.");
        if (request.Event == "attempt" ? request.Outcome is not ("succeeded" or "failed") : request.Outcome is not null)
            return CodedProblems.Validation("outcome", "Only attempts carry succeeded or failed.");
        if (request.ConstructCommit is not null && !Regex.IsMatch(request.ConstructCommit, @"\A[0-9a-fA-F]{7,64}\z"))
            return CodedProblems.Validation("constructCommit", "Expected 7–64 hex characters.");
        var now = clock.UtcNow; var at = request.At ?? now;
        var report = request.Event switch
        {
            "provisioned" => new GuestReport(request.ConstructCommit, at, null, now, GuestReportProvenance.Provisioner, null, null),
            "reinstalled" => new GuestReport(request.ConstructCommit, null, at, now, GuestReportProvenance.Provisioner, null, null),
            _ => new GuestReport(null, null, null, null, GuestReportProvenance.Unknown, at, request.Outcome),
        };
        if (!await reports.UpdateGuestReportAsync(name, report, ct)) return CodedProblems.Create(409, "vm-deleting", "VM changed during the report.");
        CodedProblems.Audit(http, "vm.guest-report", vm.Owner, vm.Parent, vm.Name, "event=" + request.Event);
        return Results.Ok((await vms.GetAsync(name, ct))!.Guest);
    }
    private static async Task<IResult> RotateAsync(string name, VmTokenRequest request, HttpContext http, IVmRepository vms, IAuthorizationService authorization,
        IVmTokenIssuer tokens, IConsoleSessionStore sessions, IVmOperationGate gate, IClock clock, CancellationToken ct)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, vms, authorization, name, Policies.VmOwnerOrAdmin, ct);
        if (!lookup.Ok) return lookup.Failure!;
        await using var handle = await gate.AcquireAsync(name, http.TraceIdentifier, ct);
        var vm = await vms.GetAsync(name, ct); if (vm is null) return Problems.NotFound("Unknown VM.");
        if (vm.Deleting) return CodedProblems.Create(409, "vm-deleting", "VM is being deleted.");
        if (vm.Kind == VmKind.Child) return CodedProblems.Create(409, "child-has-no-token", "A child never receives a credential.");
        var kind = VmTokenKind.Primary;
        if (request.Kind is not null && (!ApiHelpers.TryParseEnum(request.Kind, out kind) || request.Kind is not ("primary" or "legacy"))) return CodedProblems.Validation("kind", "Expected primary or legacy.");
        var token = await tokens.IssueVmTokenAsync(name, kind, ct); sessions.RemoveForPrincipal("vm:" + vm.Name);
        CodedProblems.Audit(http, "vm.token.rotate", vm.Owner, target: vm.Name, extra: "kind=" + kind.ToString().ToLowerInvariant());
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { vmToken = token, kind, issuedAt = clock.UtcNow });
    }
    private static async Task<IResult> RevokeAsync(string name, HttpContext http, IVmRepository vms, IAuthorizationService authorization, IVmTokenIssuer tokens, IConsoleSessionStore sessions, CancellationToken ct)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, vms, authorization, name, Policies.VmOwnerOrAdmin, ct);
        if (!lookup.Ok) return lookup.Failure!; var vm = lookup.Vm!;
        if (vm.Kind == VmKind.Child) return CodedProblems.Create(409, "child-has-no-token", "A child never receives a credential.");
        if (vm.Deleting) return CodedProblems.Create(409, "vm-deleting", "VM is being deleted.");
        if (!await tokens.RevokeVmTokenAsync(name, ct)) return CodedProblems.Create(409, "vm-deleting", "VM changed during revocation.");
        sessions.RemoveForPrincipal("vm:" + vm.Name); CodedProblems.Audit(http, "vm.token.revoke", vm.Owner, target: vm.Name);
        return Results.NoContent();
    }
    private static async Task<IResult> OverridesAsync(string name, IVmRepository vms, IVmDelegationRepository store, IDelegationPolicy policy, CancellationToken ct)
    {
        var vm = await vms.GetAsync(name, ct); if (vm is null) return Problems.NotFound("Unknown VM.");
        if (vm.Kind != VmKind.Primary) return CodedProblems.Create(409, "not-a-primary", "Overrides apply to primaries.");
        return Results.Ok(new { stored = await store.GetOverrideAsync(name, ct), effective = await HostAdminEndpoints.EffectiveAsync(vm.Owner, name, policy, ct) });
    }
    private static async Task<IResult> SetOverridesAsync(string name, VmOverrideRequest request, HttpContext http, IVmRepository vms, IVmDelegationRepository store,
        IDelegationPolicy policy, IClock clock, IVmOperationGate gate, CancellationToken ct)
    {
        await using var handle = await gate.AcquireAsync(name, http.TraceIdentifier, ct);
        var vm = await vms.GetAsync(name, ct); if (vm is null) return Problems.NotFound("Unknown VM.");
        if (vm.Kind != VmKind.Primary) return CodedProblems.Create(409, "not-a-primary", "Overrides apply to primaries.");
        if (vm.Deleting) return CodedProblems.Create(409, "vm-deleting", "VM is being deleted.");
        if (request.MaxRetainedChildren < 0 || request.MaxChildLifetimeSeconds is < 300) return CodedProblems.Validation("overrides", "Counts must be non-negative and finite lifetimes at least 300 seconds.");
        await store.SetOverrideAsync(new(name, request.AllowChildCreation, request.MaxRetainedChildren, request.MaxChildLifetimeSeconds, request.AllowNeverLifetime, request.AllowSharing, clock.UtcNow), ct);
        CodedProblems.Audit(http, "vm.overrides", vm.Owner, target: vm.Name); return await OverridesAsync(name, vms, store, policy, ct);
    }
    private static async Task<IResult> RemoveOverridesAsync(string name, HttpContext http, IVmRepository vms, IVmDelegationRepository store, CancellationToken ct)
    {
        var vm = await vms.GetAsync(name, ct); if (vm is null) return Problems.NotFound("Unknown VM.");
        if (vm.Kind != VmKind.Primary) return CodedProblems.Create(409, "not-a-primary", "Overrides apply to primaries.");
        if (vm.Deleting) return CodedProblems.Create(409, "vm-deleting", "VM is being deleted.");
        await store.RemoveOverrideAsync(name, ct); CodedProblems.Audit(http, "vm.overrides", vm.Owner, target: name); return Results.NoContent();
    }
    private static async Task<IResult> ChildrenAsync(string name, HttpContext http, IVmRepository vms, IVmDelegationRepository store, IAuthorizationService auth, VmInventoryProjection projection, CancellationToken ct)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, vms, auth, name, Policies.ChildOwnerOrAdmin, ct); if (!lookup.Ok) return lookup.Failure!;
        if (lookup.Vm!.Kind != VmKind.Primary) return CodedProblems.Create(409, "not-a-primary", "Children belong to primaries.");
        var result = new List<VmResponse>(); foreach (var vm in await store.ListChildrenAsync(name, ct)) result.Add(await projection.ProjectAsync(vm, http.User, ct)); return Results.Ok(result);
    }
    private static async Task<IResult> SharedAsync(HttpContext http, IVmDelegationRepository store, IVmRepository vms, IUserStore users, VmInventoryProjection projection, CancellationToken ct)
    {
        var result = new List<VmResponse>();
        foreach (var vm in await store.ListSharedAsync(SharingScope.Host, ct))
        {
            var relationship = await DelegationAuthorization.RelationshipAsync(http.User, vm, vms, users, ct);
            if (relationship is null or ForwardRelationship.Owner or ForwardRelationship.Parent or ForwardRelationship.Self ||
                Ownership.SameName(http.User.NameOrEmpty(), vm.Owner) || vm.Deleting || await users.GetAsync(vm.Owner, ct) is not { Enabled: true }) continue;
            result.Add((await projection.ProjectAsync(vm, http.User, ct)) with { Shared = true });
        }
        return Results.Ok(result);
    }
    private static async Task<IResult> VmCapabilitiesAsync(string name, HttpContext http, IVmRepository vms, IAuthorizationService auth, ICapabilityAggregator aggregator,
        IChildVmDriver driver, IDelegationPolicy policy, CancellationToken ct)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, vms, auth, name, Policies.ChildOperator, ct); if (!lookup.Ok) return lookup.Failure!; var vm = lookup.Vm!;
        var caps = await aggregator.GetAsync(ct);
        VmCapabilitiesSnapshot? runtime = null;
        if (caps.Generations.Count > 0) runtime = await driver.GetVmCapabilitiesAsync(name, ct);
        var effective = await policy.ResolveAsync(vm.Owner, vm.Parent, ct);
        var hostForward = vm.Kind == VmKind.Primary ? caps.Network.HostForwardPrimary : caps.Network.HostForwardChild;
        if (!effective.AllowHostForwards) hostForward = CapabilityLevel.Unsupported;
        return Results.Ok(new
        {
            vm = vm.Name,
            state = runtime?.State ?? vm.State,
            console = ConsoleCapabilitiesResponse.From(caps.Console, new ConsoleScreen(runtime?.NativeWidth ?? 0, runtime?.NativeHeight ?? 0,
                runtime?.VideoHeadPresent ?? false, runtime?.KeyboardPresent ?? false, runtime?.SyntheticMousePresent ?? false, runtime?.Ps2MousePresent ?? false),
                http.RequestServices.GetRequiredService<Constructd.Core.Configuration.ConstructdOptions>().BrowserConsoleEnabled),
            hardware = new { generation = runtime?.Generation ?? vm.Hardware?.Generation, secureBootTemplateLocked = runtime?.SecureBootTemplateLocked ?? false },
            gracefulShutdown = runtime?.GracefulShutdown ?? CapabilityLevel.Unsupported,
            network = new { clientForward = caps.Network.ClientForward, hostForward, addressVerification = caps.Network.AddressVerification }
        });
    }
}
