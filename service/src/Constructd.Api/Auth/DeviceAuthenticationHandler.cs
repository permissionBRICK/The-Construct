using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Construct.Keys;
using Constructd.Store.Client;
using Constructd.Sync.Participation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Constructd.Api.Auth;

/// <summary>
/// A device (docs/plans/construct-platform-design.md 8.1): <c>X-Construct-Device-Token</c> with a
/// proof by the device's key in <c>X-Construct-Proof</c>. The token is bound to the key of the
/// device's <c>device</c> record; a token without its key, or with another device's, fails. The
/// principal is the device's user (the account) with the device id as a claim, so every user route
/// accepts it. A request that carries a device token is judged by it whatever else it carries, so
/// the binding holds under every sign-in method; sign-in without a device token works as before.
/// </summary>
public sealed class DeviceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    EngineDevices devices)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Device";

    /// <summary>The device id of a device principal.</summary>
    public const string DeviceClaim = "constructd:device";

    public static bool Presented(HttpRequest request) => request.Headers.ContainsKey(RequestProof.DeviceTokenHeader);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Headers[RequestProof.DeviceTokenHeader].ToString();
        if (token.Length == 0) return AuthenticateResult.NoResult();
        if (token.Length > 200) return AuthenticateResult.Fail("Invalid device credential.");

        (string Account, string DeviceId)? device;
        try
        {
            device = await devices.AuthenticateAsync(token, Request.Headers[RequestProof.Header].ToString(), Context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StoreUnavailableException or CryptographicException)
        {
            return AuthenticateResult.Fail("The device records cannot be read.");
        }

        if (device is not { } found) return AuthenticateResult.Fail("Invalid device credential.");

        var identity = new ClaimsIdentity(SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, found.Account));
        identity.AddClaim(new Claim(DeviceClaim, found.DeviceId));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = SchemeName;
        return Task.CompletedTask;
    }
}
