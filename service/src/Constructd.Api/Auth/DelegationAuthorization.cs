using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Microsoft.AspNetCore.Authorization;
namespace Constructd.Api.Auth;

public sealed record DelegationRequirement(string Policy) : IAuthorizationRequirement;

public sealed class DelegationAuthorization(IVmRepository vms, IUserStore users, IDelegationPolicy policy)
    : AuthorizationHandler<DelegationRequirement, Vm>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, DelegationRequirement requirement, Vm vm)
    {
        var caller = context.User;
        if (!caller.IsKnownUser() && !caller.IsPrimaryToken()) return;
        var relationship = await RelationshipAsync(caller, vm, vms, users, CancellationToken.None);
        if (relationship is null) return;
        var own = relationship != ForwardRelationship.Shared;
        if (requirement.Policy == Policies.ParentDelegate)
        {
            if (vm.Kind != VmKind.Primary || !own || vm.Deleting || vm.ChildCreationClosed) return;
            if (!(await policy.ResolveAsync(vm.Owner, vm.Name, CancellationToken.None)).AllowChildCreation) return;
        }
        else if (requirement.Policy == Policies.ChildOwnerOrAdmin && !own) return;
        context.Succeed(requirement);
    }

    public static async Task<ForwardRelationship?> RelationshipAsync(System.Security.Claims.ClaimsPrincipal caller, Vm vm,
        IVmRepository vms, IUserStore users, CancellationToken ct)
    {
        if (caller.IsAdmin()) return ForwardRelationship.Admin;
        if (caller.IsKnownUser() && Ownership.SameName(caller.NameOrEmpty(), vm.Owner)) return ForwardRelationship.Owner;
        if (caller.IsPrimaryToken())
        {
            var parent = await vms.GetAsync(caller.VmTokenName()!, ct);
            if (parent is not { Kind: VmKind.Primary, TokenKind: VmTokenKind.Primary, Deleting: false } ||
                await users.GetAsync(parent.Owner, ct) is not { Enabled: true }) return null;
            if (Ownership.SameName(vm.Name, parent.Name)) return ForwardRelationship.Self;
            if (Ownership.SameName(vm.Parent, parent.Name)) return ForwardRelationship.Parent;
        }
        if ((caller.IsKnownUser() || caller.IsPrimaryToken()) && vm.Kind == VmKind.Child && vm.Sharing == SharingScope.Host &&
            !vm.Deleting && await users.GetAsync(vm.Owner, ct) is { Enabled: true }) return ForwardRelationship.Shared;
        return null;
    }
}
