using System.Security.Claims;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Logic;
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
public sealed class UserClaimsTransformation(IUserStore users, ConstructdOptions? options = null) : IClaimsTransformation
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

        // Kerberos on a Linux host names the user `user@REALM`; the store (and every Windows host)
        // knows them as `DOMAIN\user`. One spelling from here on, so `whoami`, the audit trail and
        // the user lookup agree — see PrincipalNames.
        var negotiate = options?.Negotiate;
        var mapped = PrincipalNames.Normalize(name, negotiate?.DomainName, negotiate?.Realm);
        var renamed = !string.Equals(mapped, name, StringComparison.Ordinal);

        var user = await users.GetAsync(mapped, CancellationToken.None).ConfigureAwait(false);
        if ((user is null || !user.Enabled) && !renamed)
        {
            return principal;
        }

        // Clone: the transformation may run more than once per request on a cached principal.
        var transformed = new ClaimsPrincipal(principal.Identities.Select(i => i.Clone()));
        var identity = (ClaimsIdentity)transformed.Identity!;
        if (renamed)
        {
            foreach (var claim in identity.FindAll(identity.NameClaimType).ToList())
            {
                identity.RemoveClaim(claim);
            }

            identity.AddClaim(new Claim(identity.NameClaimType, mapped));
        }

        if (user is { Enabled: true })
        {
            identity.AddClaim(new Claim(ConstructdClaims.KnownUser, "true"));
            identity.AddClaim(new Claim(identity.RoleClaimType, user.Role.ToString()));
        }

        return transformed;
    }
}
