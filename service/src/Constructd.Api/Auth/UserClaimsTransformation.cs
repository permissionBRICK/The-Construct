using System.Security.Claims;
using Constructd.Core.Abstractions;
using Microsoft.AspNetCore.Authentication;

namespace Constructd.Api.Auth;

/// <summary>
/// Maps an authenticated identity (token, Negotiate, or the fake test identity) onto its
/// <see cref="Core.Domain.User"/> record and stamps role + "known user" claims on it.
///
/// This is where "users are created only by an admin" is enforced: an identity that authenticates
/// but has no user record stays authenticated and gets no claims, so every policy except
/// <c>authenticated</c> rejects it — <c>GET /whoami</c> still answers, which is how enrollment
/// reports "you are not enrolled on this host".
/// </summary>
public sealed class UserClaimsTransformation(IUserStore users) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity is not { IsAuthenticated: true })
        {
            return principal;
        }

        // VM-scoped tokens are not users and must never gain a role.
        if (principal.IsVmToken() || principal.IsKnownUser())
        {
            return principal;
        }

        var name = principal.Identity.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            return principal;
        }

        var user = await users.GetAsync(name, CancellationToken.None).ConfigureAwait(false);
        if (user is null)
        {
            return principal;
        }

        // Clone: the transformation may run more than once per request on a cached principal.
        var transformed = principal.Clone();
        var identity = (ClaimsIdentity)transformed.Identity!;
        identity.AddClaim(new Claim(ConstructdClaims.KnownUser, "true"));
        identity.AddClaim(new Claim(identity.RoleClaimType, user.Role.ToString()));
        return transformed;
    }
}
