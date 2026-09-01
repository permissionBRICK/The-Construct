using System.Security.Claims;
using System.Text.Encodings.Web;
using Constructd.Core.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Constructd.Api.Auth;

/// <summary>
/// <c>Authorization: Bearer &lt;secret&gt;</c> — admin-issued user tokens. The role is not taken from
/// the token; <see cref="UserClaimsTransformation"/> resolves it from the user store on every
/// request, so a role change or a deleted user takes effect immediately.
/// </summary>
public sealed class BearerTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ITokenService tokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!AuthorizationHeader.TryRead(Request, ConstructdSchemes.Bearer, out var secret))
        {
            return AuthenticateResult.NoResult();
        }

        var principal = await tokens.ValidateAsync(secret, Context.RequestAborted).ConfigureAwait(false);
        if (principal is null || principal.Kind != TokenKind.User)
        {
            // Deliberately vague: never echo anything derived from the presented secret.
            return AuthenticateResult.Fail("Invalid API token.");
        }

        var identity = new ClaimsIdentity(ConstructdSchemes.Bearer, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, principal.Name));
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = ConstructdSchemes.Bearer;
        return Task.CompletedTask;
    }
}

/// <summary>
/// <c>Authorization: VmToken &lt;secret&gt;</c> — the scoped token injected into a VM at provision
/// time (plan §4.6). It authorizes nothing but its own VM's forwards and activity heartbeat; the
/// principal deliberately has no <see cref="ConstructdClaims.KnownUser"/> claim, so every
/// user-facing policy rejects it.
/// </summary>
public sealed class VmTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ITokenService tokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!AuthorizationHeader.TryRead(Request, ConstructdSchemes.VmToken, out var secret))
        {
            return AuthenticateResult.NoResult();
        }

        var principal = await tokens.ValidateAsync(secret, Context.RequestAborted).ConfigureAwait(false);
        if (principal is not { Kind: TokenKind.Vm, VmName: not null })
        {
            return AuthenticateResult.Fail("Invalid VM token.");
        }

        var identity = new ClaimsIdentity(ConstructdSchemes.VmToken, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, principal.Name));
        identity.AddClaim(new Claim(ConstructdClaims.VmName, principal.VmName));
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = ConstructdSchemes.VmToken;
        return Task.CompletedTask;
    }
}

/// <summary>
/// ⚠ Stand-in for Negotiate so the Windows-identity paths can be tested off Windows: it trusts the
/// <c>X-Constructd-Test-Identity</c> header. It is registered ONLY in fake mode
/// (<c>Constructd:Fake=true</c>) — see <see cref="AuthenticationSetup"/> — and must never be enabled
/// on a real host.
/// </summary>
public sealed class TestIdentityAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var name = Request.Headers[ConstructdHeaders.TestIdentity].ToString();
        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(ConstructdSchemes.TestIdentity, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, name.Trim()));
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = ConstructdSchemes.TestIdentity;
        return Task.CompletedTask;
    }
}

/// <summary>Parsing of <c>Authorization: &lt;scheme&gt; &lt;secret&gt;</c>.</summary>
internal static class AuthorizationHeader
{
    public static bool TryRead(HttpRequest request, string scheme, out string secret)
    {
        secret = string.Empty;
        var header = request.Headers.Authorization.ToString();

        if (string.IsNullOrWhiteSpace(header) ||
            !header.StartsWith(scheme + ' ', StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        secret = header[(scheme.Length + 1)..].Trim();
        return secret.Length > 0;
    }

    /// <summary>The scheme token of the Authorization header, or empty when there is none.</summary>
    public static string SchemeOf(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return string.Empty;
        }

        var space = header.IndexOf(' ', StringComparison.Ordinal);
        return space <= 0 ? header : header[..space];
    }
}
