using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;

namespace Constructd.Api.Endpoints;

/// <summary><c>GET /whoami</c> — resolved identity + role, the enrollment probe of plan §4.5.</summary>
public static class IdentityEndpoints
{
    public static RouteGroupBuilder MapIdentityEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/whoami", WhoAmIAsync)
            .RequireAuthorization(Policies.AnyUserIdentity)
            .WithName("WhoAmI");

        return api;
    }

    /// <remarks>
    /// Deliberately reachable by any authenticated user identity, enrolled or not: this is how the
    /// installer tells "your Kerberos ticket works but the admin has not added you" from "your
    /// credential is wrong". VM-scoped tokens are refused here like everywhere outside their own VM's
    /// forwards and heartbeat.
    /// </remarks>
    private static async Task<IResult> WhoAmIAsync(
        HttpContext http,
        IUserStore users,
        CancellationToken cancellationToken)
    {
        var principal = http.User;
        var scheme = principal.Identity?.AuthenticationType ?? "unknown";

        var user = await users.GetAsync(principal.NameOrEmpty(), cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new WhoAmIResponse(
            Name: principal.NameOrEmpty(),
            Kind: "user",
            Scheme: scheme,
            Known: user is not null,
            Role: user?.Role,
            MaxVms: user?.MaxVms,
            AllowHostForwards: user?.AllowHostForwards));
    }
}
