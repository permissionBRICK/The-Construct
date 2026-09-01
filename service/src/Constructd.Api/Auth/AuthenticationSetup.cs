using Constructd.Core.Configuration;
using Microsoft.AspNetCore.Authentication;

namespace Constructd.Api.Auth;

/// <summary>
/// Wires the authentication schemes. The scheme actually used is picked per request from the
/// <c>Authorization</c> header, so token clients and Windows-identity clients share one API.
///
/// Negotiate (Kerberos/NTLM, plan §2 "Kerberos first") is registered ONLY on Windows — that is the
/// seam the remote-driver batch (B7) builds its client auth against. Off Windows the fake
/// <see cref="ConstructdSchemes.TestIdentity"/> scheme takes its place, but only in fake mode.
/// </summary>
public static class AuthenticationSetup
{
    public static IServiceCollection AddConstructdAuthentication(
        this IServiceCollection services,
        ConstructdOptions options)
    {
        // In fake mode the Windows-identity path is simulated by a header-based scheme so the
        // ownership/role logic can be exercised on Linux. Never enabled on a real host.
        var testIdentityEnabled = options.Fake;

        var builder = services.AddAuthentication(ConstructdSchemes.Default);

        builder.AddPolicyScheme(ConstructdSchemes.Default, ConstructdSchemes.Default, scheme =>
        {
            scheme.ForwardDefaultSelector = context =>
            {
                var authScheme = AuthorizationHeader.SchemeOf(context.Request);

                if (string.Equals(authScheme, ConstructdSchemes.VmToken, StringComparison.OrdinalIgnoreCase))
                {
                    return ConstructdSchemes.VmToken;
                }

                if (string.Equals(authScheme, ConstructdSchemes.Bearer, StringComparison.OrdinalIgnoreCase))
                {
                    return ConstructdSchemes.Bearer;
                }

                if (testIdentityEnabled && context.Request.Headers.ContainsKey(ConstructdHeaders.TestIdentity))
                {
                    return ConstructdSchemes.TestIdentity;
                }

                // No usable credential: challenge with the host's primary scheme.
                if (OperatingSystem.IsWindows())
                {
                    return Microsoft.AspNetCore.Authentication.Negotiate.NegotiateDefaults.AuthenticationScheme;
                }

                return testIdentityEnabled ? ConstructdSchemes.TestIdentity : ConstructdSchemes.Bearer;
            };
        });

        builder.AddScheme<AuthenticationSchemeOptions, BearerTokenAuthenticationHandler>(
            ConstructdSchemes.Bearer, displayName: "API token", configureOptions: null);

        builder.AddScheme<AuthenticationSchemeOptions, VmTokenAuthenticationHandler>(
            ConstructdSchemes.VmToken, displayName: "VM-scoped token", configureOptions: null);

        if (testIdentityEnabled)
        {
            // The warning about this being enabled is logged once at startup (see Program.cs).
            builder.AddScheme<AuthenticationSchemeOptions, TestIdentityAuthenticationHandler>(
                ConstructdSchemes.TestIdentity, displayName: "Test identity", configureOptions: null);
        }

        // ---- Windows-only seam -------------------------------------------------------------
        // Kerberos/NTLM for the VS Code extension's and PowerShell's process identity. Requires the
        // host to be domain-joined; the identity is then mapped to a user record by
        // UserClaimsTransformation exactly like a token identity.
        if (OperatingSystem.IsWindows())
        {
            builder.AddNegotiate();
        }
        // ------------------------------------------------------------------------------------

        // Scoped: the user store it reads may be a per-request (database-backed) service.
        services.AddScoped<IClaimsTransformation, UserClaimsTransformation>();
        return services;
    }
}
