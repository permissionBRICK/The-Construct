using System.Security.Claims;
using System.Text.Encodings.Web;
using Constructd.Core.Abstractions;
using Constructd.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Constructd.Api.Auth;

/// <summary>
/// <c>Authorization: VaultDevice &lt;secret&gt;</c> — a phone paired for key-vault approvals
/// (docs/plans/key-vault-hosted.md). The credential is honoured ONLY on the approval and file-decision
/// routes and <c>GET /vault/device</c>; on every other path it fails authentication, so it can never
/// read values, list leases, sync or pair devices. The principal carries no user name claim the claims
/// transformation could map to an enrolled user.
/// </summary>
public sealed class VaultDeviceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    VaultHostService vault,
    IUserStore users)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static readonly string[] Routes = ["/api/v1/vault/approvals", "/api/v1/vault/files"];

    /// <summary>The paths a device credential is valid on.</summary>
    public static bool Accepts(PathString path) =>
        path.Equals("/api/v1/vault/device", StringComparison.OrdinalIgnoreCase) ||
        Routes.Any(route => path.StartsWithSegments(route, StringComparison.OrdinalIgnoreCase, out var rest) &&
                            (!rest.HasValue || rest.Value!.Count(c => c == '/') == 1));

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!AuthorizationHeader.TryRead(Request, ConstructdSchemes.VaultDevice, out var secret))
        {
            return AuthenticateResult.NoResult();
        }

        if (!Accepts(Request.Path))
        {
            return AuthenticateResult.Fail("A vault device credential is not valid for this route.");
        }

        var device = await vault.AuthenticateDeviceAsync(secret, Context.RequestAborted).ConfigureAwait(false);
        if (device is null || await users.GetAsync(device.Owner, Context.RequestAborted).ConfigureAwait(false) is not { Enabled: true })
        {
            return AuthenticateResult.Fail("Invalid vault device credential.");
        }

        var identity = new ClaimsIdentity(ConstructdSchemes.VaultDevice, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, $"vault-device:{device.Id}@{device.Owner}"));
        identity.AddClaim(new Claim(ConstructdClaims.VaultDevice, device.Id));
        identity.AddClaim(new Claim(ConstructdClaims.VaultOwner, device.Owner));
        identity.AddClaim(new Claim(ConstructdClaims.VaultDeviceLabel, device.Label));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = ConstructdSchemes.VaultDevice;
        return Task.CompletedTask;
    }
}
