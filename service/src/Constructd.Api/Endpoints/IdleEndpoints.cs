using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Microsoft.AspNetCore.Authorization;

namespace Constructd.Api.Endpoints;

/// <summary>
/// Idle policy and the in-guest activity heartbeat (plan §4.7). The policy is the user's to set
/// (within the admin cap); the heartbeat is posted by the guest with its scoped token.
/// </summary>
public static class IdleEndpoints
{
    public static RouteGroupBuilder MapIdleEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/vms/{name}/idle-policy", GetPolicyAsync)
            .RequireAuthorization(Policies.User)
            .WithName("GetIdlePolicy");

        api.MapPut("/vms/{name}/idle-policy", PutPolicyAsync)
            .RequireAuthorization(Policies.User)
            .Audited("vm.idle-policy")
            .WithName("PutIdlePolicy");

        // The one write a VM-scoped token is allowed besides its forwards.
        api.MapPost("/vms/{name}/activity", PostActivityAsync)
            .RequireAuthorization(Policies.VmScoped)
            .Audited("vm.activity")
            .WithName("PostActivity");

        return api;
    }

    private static async Task<IResult> GetPolicyAsync(
        string name,
        HttpContext http,
        IVmRepository repository,
        IAuthorizationService authorization,
        ConstructdOptions options,
        CancellationToken cancellationToken)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, repository, authorization, name,
            Policies.VmOwnerOrAdmin, cancellationToken).ConfigureAwait(false);

        if (!lookup.Ok)
        {
            return lookup.Failure!;
        }

        // Report the effective policy: the cap may have been lowered after it was stored.
        var stored = lookup.Vm!.IdlePolicy;
        var effective = IdlePolicyRules.Clamp(stored, options.Idle);
        return TypedResults.Ok(ApiHelpers.ToResponse(effective, options.Idle, clamped: effective != stored));
    }

    private static async Task<IResult> PutPolicyAsync(
        string name,
        IdlePolicyRequest? request,
        HttpContext http,
        IVmRepository repository,
        IAuthorizationService authorization,
        ConstructdOptions options,
        CancellationToken cancellationToken)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, repository, authorization, name,
            Policies.VmOwnerOrAdmin, cancellationToken).ConfigureAwait(false);

        if (!lookup.Ok)
        {
            return lookup.Failure!;
        }

        if (ApiHelpers.FenceDeleting(lookup.Vm!) is { } fenced)
        {
            http.SetAuditDetail("vm is being deleted");
            return fenced;
        }

        if (request is null)
        {
            return Problems.BadRequest("A body with 'timeoutMinutes' and 'action' is required.");
        }

        if (!VmEndpoints.TryReadPolicy(request, options, out var policy, out var error))
        {
            return error;
        }

        var clamped = policy.TimeoutMinutes != request.TimeoutMinutes ||
                      !string.Equals(policy.Action.ToString(), request.Action, StringComparison.OrdinalIgnoreCase);

        var vm = lookup.Vm!;
        await repository.UpdateAsync(vm with { IdlePolicy = policy }, cancellationToken).ConfigureAwait(false);

        http.SetAuditDetail($"timeoutMinutes={policy.TimeoutMinutes}, action={policy.Action}, clamped={clamped}");
        return TypedResults.Ok(ApiHelpers.ToResponse(policy, options.Idle, clamped));
    }

    /// <remarks>
    /// <c>busy</c> keeps the VM alive even with zero connections — a long unattended agent job is
    /// exactly the case the idle engine must not kill. Heartbeats are audited like every other
    /// mutation; they are frequent, so <c>GET /audit</c> is paged and the retention of the audit
    /// trail is an admin concern.
    /// </remarks>
    private static async Task<IResult> PostActivityAsync(
        string name,
        ActivityRequest? request,
        HttpContext http,
        IVmRepository repository,
        IAuthorizationService authorization,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, repository, authorization, name,
            Policies.VmSelfOrOwnerOrAdmin, cancellationToken).ConfigureAwait(false);

        if (!lookup.Ok)
        {
            return lookup.Failure!;
        }

        if (ApiHelpers.FenceDeleting(lookup.Vm!) is { } fenced)
        {
            http.SetAuditDetail("vm is being deleted");
            return fenced;
        }

        if (request?.Busy is not bool busy)
        {
            return Problems.BadRequest("'busy' is required.");
        }

        var reasons = (request.Reasons ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Take(20)
            .ToList();

        await repository.SaveActivityAsync(
            new ActivityReport(lookup.Vm!.Name, busy, reasons, clock.UtcNow),
            cancellationToken).ConfigureAwait(false);

        http.SetAuditDetail($"busy={busy}, reasons={reasons.Count}");
        return TypedResults.NoContent();
    }
}
