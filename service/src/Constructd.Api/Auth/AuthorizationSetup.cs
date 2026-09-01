using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Microsoft.AspNetCore.Authorization;

namespace Constructd.Api.Auth;

/// <summary>Policy names used on the routes.</summary>
public static class Policies
{
    /// <summary>
    /// Any authenticated <em>user</em> identity, enrolled on this host or not (<c>/whoami</c> only,
    /// so an identity can find out that an admin still has to add it). VM-scoped tokens are rejected:
    /// they are valid for their own VM's forwards and heartbeat and for nothing else.
    /// </summary>
    public const string AnyUserIdentity = "any-user-identity";

    /// <summary>An enrolled user. VM-scoped tokens are rejected.</summary>
    public const string User = "user";

    /// <summary>An enrolled admin.</summary>
    public const string Admin = "admin";

    /// <summary>An enrolled user OR a VM-scoped token — the per-VM routes a guest may call.</summary>
    public const string VmScoped = "vm-scoped";

    /// <summary>Resource policy over a <see cref="Vm"/>: owner or admin.</summary>
    public const string VmOwnerOrAdmin = "vm-owner-or-admin";

    /// <summary>Resource policy over a <see cref="Vm"/>: owner, admin, or that VM's own token.</summary>
    public const string VmSelfOrOwnerOrAdmin = "vm-self-or-owner-or-admin";
}

/// <summary>Resource-based requirement evaluated against a <see cref="Vm"/>.</summary>
/// <param name="AllowVmToken">Whether the VM's own scoped token satisfies the requirement.</param>
public sealed record VmAccessRequirement(bool AllowVmToken) : IAuthorizationRequirement;

/// <summary>
/// Implements the ownership rules of plan §4.4/§4.6 on top of the pure
/// <see cref="Ownership"/> helpers: a user reaches their own VMs, an admin reaches all, and a
/// VM-scoped token reaches exactly the VM it was issued for.
/// </summary>
public sealed class VmAccessHandler : AuthorizationHandler<VmAccessRequirement, Vm>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        VmAccessRequirement requirement,
        Vm resource)
    {
        var principal = context.User;

        if (principal.IsVmToken())
        {
            if (requirement.AllowVmToken && Ownership.VmTokenCanAccessVm(principal.VmTokenName(), resource.Name))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }

        if (principal.IsKnownUser() &&
            Ownership.CanAccessVm(principal.NameOrEmpty(), principal.RoleOrDefault(), resource))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Registers the authorization policies.</summary>
public static class AuthorizationSetup
{
    public static IServiceCollection AddConstructdAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, VmAccessHandler>();

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.AnyUserIdentity, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => !context.User.IsVmToken()))
            .AddPolicy(Policies.User, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(ConstructdClaims.KnownUser))
            .AddPolicy(Policies.Admin, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(ConstructdClaims.KnownUser)
                .RequireRole(nameof(Role.Admin)))
            .AddPolicy(Policies.VmScoped, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.IsKnownUser() || context.User.IsVmToken()))
            .AddPolicy(Policies.VmOwnerOrAdmin, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new VmAccessRequirement(AllowVmToken: false)))
            .AddPolicy(Policies.VmSelfOrOwnerOrAdmin, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new VmAccessRequirement(AllowVmToken: true)))
            // Anything that forgets to name a policy still needs an enrolled user.
            .SetDefaultPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireClaim(ConstructdClaims.KnownUser)
                .Build());

        return services;
    }
}
