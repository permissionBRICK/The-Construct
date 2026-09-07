using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
namespace Constructd.Api.Auth;

public sealed class UpdateHandoffAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder,
    IUpdaterLauncher launcher,IReleaseInfo release,IMaintenanceGate gate) : AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
{
    public const string SchemeName="UpdateHandoff";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if(Request.Path!="/api/v1/health" || !HttpMethods.IsGet(Request.Method) ||
            Context.Connection.RemoteIpAddress is not {} ip || !IPAddress.IsLoopback(ip) || gate.State!=MaintenanceState.Maintenance)
            return AuthenticateResult.NoResult();
        var header=Request.Headers.Authorization.ToString();
        if(!header.StartsWith(SchemeName+" ",StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var handoff=await launcher.ReadOwnHandoffAsync(release.Installed.Commit,Context.RequestAborted);
        if(handoff is null || handoff.HealthToken.Length!=64) return AuthenticateResult.Fail("Invalid update credential.");
        var given=Encoding.UTF8.GetBytes(header[(SchemeName.Length+1)..]);
        if(!CryptographicOperations.FixedTimeEquals(given,Encoding.UTF8.GetBytes(handoff.HealthToken))) return AuthenticateResult.Fail("Invalid update credential.");
        // No name or role: the health credential can never become an enrolled user.
        return AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity([],SchemeName)),SchemeName));
    }
}
