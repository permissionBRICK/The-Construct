using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Microsoft.AspNetCore.Authorization;
namespace Constructd.Api.Auth;

public sealed record ForwardRequesterRequirement : IAuthorizationRequirement;
public sealed class ForwardRequesterHandler(IVmRepository vms, IUserStore users)
    : AuthorizationHandler<ForwardRequesterRequirement, Vm>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ForwardRequesterRequirement requirement, Vm vm)
    {
        if (vm.Kind == VmKind.Primary)
        {
            // Preserve all legacy principal types and the exact old resource check.
            var old = new VmAccessRequirement(true);
            var legacy = new AuthorizationHandlerContext([old], context.User, vm);
            await new VmAccessHandler().HandleAsync(legacy);
            if (legacy.HasSucceeded) context.Succeed(requirement);
        }
        else if ((!context.User.IsVmToken() || context.User.IsPrimaryToken()) &&
            await DelegationAuthorization.RelationshipAsync(context.User, vm, vms, users, CancellationToken.None) is not null)
            context.Succeed(requirement);
    }
    public static string Requester(System.Security.Claims.ClaimsPrincipal caller) =>
        caller.IsVmToken() ? "vm:" + caller.VmTokenName() : caller.NameOrEmpty();
}
