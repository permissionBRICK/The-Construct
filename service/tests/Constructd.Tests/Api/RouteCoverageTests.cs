using Constructd.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Api;

/// <summary>
/// Guards the API surface itself: the exact route set of plan §4.4, and the invariant that no route
/// is reachable without authorization (a forgotten <c>RequireAuthorization</c> is otherwise silent).
/// </summary>
public class RouteCoverageTests
{
    private static readonly string[] ExpectedRoutes =
    [
        "POST /api/v1/vms/{name}/source",
        "GET /api/v1/vms/{name}/source/{commit}",
        "GET /api/v1/host/source-cache",
        "POST /api/v1/host/source-cache/cleanup",
        "DELETE /api/v1/host/source-cache/{commit}",
        "GET /api/v1/host/updates/status",
        "POST /api/v1/host/updates/check",
        "POST /api/v1/host/updates/stage",
        "POST /api/v1/host/updates/apply",
        "POST /api/v1/host/updates/cancel",
        "POST /api/v1/host/updates/resolve",
        "POST /api/v1/host/updates/run",
        "GET /api/v1/me/updates",
        "PUT /api/v1/me/updates",
        "POST /api/v1/me/vms/reprovision",
        "POST /api/v1/host/vms/reprovision",
        "GET /api/v1/vms/{name}/update",
        "POST /api/v1/vms/{name}/update",
        "POST /api/v1/media/acquire",
        "POST /api/v1/media/acquire-windows",
        "POST /api/v1/media/{id}/prepare-windows",
            "GET /api/v1/host/windows/",
        "POST /api/v1/host/windows/keys",
        "DELETE /api/v1/host/windows/keys/{id}",
        "POST /api/v1/host/windows/guests/{name}/assign",
        "POST /api/v1/host/windows/guests/{name}/reactivate",
        "GET /api/v1/vms/{name}/windows",
        "POST /api/v1/media/uploads",
        "PUT /api/v1/media/uploads/{id}/chunks/{index:int}",
        "GET /api/v1/media/uploads/{id}",
        "POST /api/v1/media/uploads/{id}/complete",
        "DELETE /api/v1/media/uploads/{id}",
        "GET /api/v1/media",
        "GET /api/v1/media/{id}",
        "GET /api/v1/media/{id}/references",
        "DELETE /api/v1/media/{id}",
        "POST /api/v1/media/cleanup",
        "GET /api/v1/vms/{name}/console/capabilities",
        "POST /api/v1/vms/{name}/console/sessions",
        "POST /api/v1/vms/{name}/console/sessions/{sid}/renew",
        "POST /api/v1/vms/{name}/console/sessions/{sid}/connection",
        "DELETE /api/v1/vms/{name}/console/sessions/{sid}",
        "GET /api/v1/vms/{name}/console/sessions/{sid}/screenshot",
        "POST /api/v1/vms/{name}/console/sessions/{sid}/keyboard",
        "POST /api/v1/vms/{name}/console/sessions/{sid}/mouse",
        "GET /api/v1/whoami",
        "GET /api/v1/store/records",
        "GET /api/v1/store/records/{id}",
        "GET /api/v1/store/records/{id}/history",
        "PUT /api/v1/store/records/{id}",
        "DELETE /api/v1/store/records/{id}",
        "POST /api/v1/store/records/batch",
        "GET /api/v1/store/epochs",
        "GET /api/v1/store/export",
        "POST /api/v1/store/import",
        "POST /api/v1/store/devices",
        "GET /api/v1/store/devices",
        "DELETE /api/v1/store/devices/{id}",
        "POST /api/v1/store/devices/{id}/acknowledge",
        "POST /api/v1/store/engines",
        "GET /api/v1/store/keys",
        "PUT /api/v1/store/keys",
        "PUT /api/v1/store/recovery",
        "POST /api/v1/store/recovery",
        "POST /api/v1/store/pairing",
        "GET /api/v1/store/pairing/{id}",
        "PUT /api/v1/store/pairing/{id}",
        "GET /api/v1/store/pairing/{id}/grant",
        "GET /api/v1/engine",
        "POST /api/v1/devices/enroll",
        "DELETE /api/v1/devices/{id}",
        "PUT /api/v1/engine/grants",
        "GET /api/v1/sync/records/{id}",
        "GET /api/v1/health",
        "GET /api/v1/host/status",
        "GET /api/v1/host/iso-catalog",
        "GET /api/v1/host/capacity",
        "GET /api/v1/host/capabilities",
        "GET /api/v1/host/config",
        "GET /api/v1/host/https",
        "PUT /api/v1/host/https/certificate",
        "DELETE /api/v1/host/https/certificate",
        "PUT /api/v1/host/config",
        "GET /api/v1/users",
        "GET /api/v1/users/{name}",
        "PUT /api/v1/users/{name}",
        "GET /api/v1/users/{name}/allowance",
        "PUT /api/v1/users/{name}/allowance",
        "GET /api/v1/users/{name}/tokens",
        "DELETE /api/v1/users/{name}/tokens/{id}",
        "GET /api/v1/vms/{name}/identity",
        "GET /api/v1/vms/{name}/nested",
        "PUT /api/v1/vms/{name}/nested",
        "GET /api/v1/vms/{name}/memory-floor",
        "PUT /api/v1/vms/{name}/memory-floor",
        "POST /api/v1/vms/{name}/guest-report",
        "POST /api/v1/vms/{name}/token",
        "DELETE /api/v1/vms/{name}/token",
        "GET /api/v1/vms/{name}/overrides",
        "PUT /api/v1/vms/{name}/overrides",
        "DELETE /api/v1/vms/{name}/overrides",
        "GET /api/v1/vms/{name}/children",
        "GET /api/v1/vms/shared",
        "GET /api/v1/vms/{name}/addresses",
            "GET /api/v1/vms/{name}/capabilities",
        "POST /api/v1/users",
        "DELETE /api/v1/users/{name}",
        "POST /api/v1/users/{name}/tokens",
        "GET /api/v1/audit",
        "GET /api/v1/vms",
        "GET /api/v1/vm-defaults",
        "GET /api/v1/vms/{name}/cpu",
        "GET /api/v1/vms/{name}/memory",
        "GET /api/v1/vms/{name}/network",
        "PUT /api/v1/vms/{name}/cpu",
        "PUT /api/v1/vms/{name}/memory",
        "PUT /api/v1/vms/{name}/network",
        "POST /api/v1/vms",
        "GET /api/v1/vms/{name}",
        "DELETE /api/v1/vms/{name}",
        "POST /api/v1/vms/{name}/power",
        "POST /api/v1/vms/{parent}/children",
        "GET /api/v1/vms/{name}/state",
        "GET /api/v1/vms/{name}/endpoint",
        "GET /api/v1/vms/{name}/forwards",
        "GET /api/v1/vms/{name}/https",
        "POST /api/v1/vms/{name}/forwards",
        "DELETE /api/v1/vms/{name}/forwards/{id}",
        "POST /api/v1/vms/{name}/forwards/{id}/ack",
        "GET /api/v1/vms/{name}/idle-policy",
        "PUT /api/v1/vms/{name}/idle-policy",
        "POST /api/v1/vms/{name}/activity",
        "POST /api/v1/vms/{name}/usage",
        "GET /api/v1/vms/{name}/usage",
        "POST /api/v1/vms/{name}/jobs",
        "GET /api/v1/vms/{name}/provision-status",
        "GET /api/v1/vms/{name}/desired",
        "PUT /api/v1/vms/{name}/desired",
        "GET /api/v1/host/usage",
        "GET /api/v1/jobs",
        "POST /api/v1/jobs/{id}/cancel",
        "POST /api/v1/jobs/{id}/retry",
        "POST /api/v1/vms/{name}/lifecycle",
        "POST /api/v1/vms/{name}/lease",
        "PUT /api/v1/vms/{name}/sharing",
        "PUT /api/v1/vms/{name}/hardware",
        "PUT /api/v1/vms/{name}/media",
        "POST /api/v1/vms/{name}/media/release",
        "GET /api/v1/jobs/{id}",
        "GET /api/v1/jobs/{id}/events",
        "GET /api/v1/vault/entries",
        "PUT /api/v1/vault/entries",
        "PUT /api/v1/vault/settings",
        "POST /api/v1/vms/{name}/vault/unlock",
        "GET /api/v1/vault/leases",
        "DELETE /api/v1/vault/leases/{id}",
        "GET /api/v1/vault/devices",
        "POST /api/v1/vault/devices",
        "DELETE /api/v1/vault/devices/{id}",
        "GET /api/v1/vault/activity",
        "GET /api/v1/vault/mode",
        "PUT /api/v1/vault/mode",
        "GET /api/v1/vault/entry-requests",
        "POST /api/v1/vault/entry-requests/{id}",
        "POST /api/v1/vault/entry-requests/{id}/decline",
        "GET /api/v1/vault/approvals",
        "POST /api/v1/vault/approvals/{id}",
        "GET /api/v1/vault/files",
        "POST /api/v1/vault/files/{id}",
        "GET /api/v1/vault/device",
        "POST /api/v1/vms/{name}/vault/requests",
        "GET /api/v1/vms/{name}/vault/requests/{id}",
        "GET /api/v1/vms/{name}/vault/scrubs",
        "POST /api/v1/vms/{name}/vault/scrubs/{id}",
        "GET /vault/",
        "GET /vault/pair",
        "GET /vault/vault.js",
        "GET /vault/vault.css",
        "POST /api/v1/vms/{name}/backups",
        "PUT /api/v1/vms/{name}/backups/{id:regex(^[0-9a-f]{{32}}$)}/keys",
        "PUT /api/v1/vms/{name}/backups/{id:regex(^[0-9a-f]{{32}}$)}/content",
        "DELETE /api/v1/vms/{name}/backups/{id:regex(^[0-9a-f]{{32}}$)}",
        "POST /api/v1/vms/{name}/backups/report",
        "POST /api/v1/vms/{name}/backups/{id:regex(^[0-9a-f]{{32}}$)}/restored",
        "POST /api/v1/vms/{name}/backups/{id:regex(^[0-9a-f]{{32}}$)}/pin",
        "DELETE /api/v1/vms/{name}/backups/{id:regex(^[0-9a-f]{{32}}$)}/pin",
        "GET /api/v1/vms/{name}/backups",
        "GET /api/v1/vms/{name}/backups/{id:regex(^[0-9a-f]{{32}}$)}/content",
        "GET /api/v1/vms/{name}/backups/{id:regex(^[0-9a-f]{{32}}$)}/keys",
        "GET /api/v1/vms/{name}/backups/status",
        "POST /api/v1/vms/{name}/backups/request",
        "GET /api/v1/vms/{name}/backups/settings",
        "PUT /api/v1/vms/{name}/backups/settings",
    ];

    // The key vault's phone pages: anonymous static files, outside /api/v1.
    /// <summary>Routes open to anyone: they answer an anonymous request with something other than a challenge.</summary>
    private static readonly string[] OpenRoutes =
    [
        "GET /api/v1/health", "GET /vault/", "GET /vault/pair", "GET /vault/vault.js", "GET /vault/vault.css",
        // Device registration, recovery and pairing: the credential is a code, a key proof or nothing (plan 8.3, 8.5).
        "POST /api/v1/store/devices", "POST /api/v1/store/recovery", "POST /api/v1/store/pairing", "GET /api/v1/store/pairing/{id}/grant",
        "POST /api/v1/devices/enroll",
    ];

    /// <summary>
    /// Routes without an authorization policy: the open ones and the store's, which resolves its own
    /// callers from the tokens it issued and challenges an anonymous caller itself.
    /// </summary>
    private static readonly string[] AnonymousRoutes = [.. OpenRoutes.Concat(ExpectedRoutes.Where(r => r.Contains(" /api/v1/store/", StringComparison.Ordinal))).Distinct()];

    private static List<(string Route, RouteEndpoint Endpoint)> Routes(TestApp app) =>
        app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => (
                Route: $"{string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])} " +
                       $"/{endpoint.RoutePattern.RawText?.TrimStart('/')}",
                Endpoint: endpoint))
            .ToList();

    [Fact]
    public async Task EveryProtectedRouteChallengesAnonymousRequests()
    {
        using var app = new TestApp(); using var anonymous = app.CreateAnonymousClient();
        foreach (var route in Routes(app).Where(r => !OpenRoutes.Contains(r.Route)))
        {
            var parts = route.Route.Split(' ', 2);
            // A parameter may carry a regex constraint with escaped braces ({{ }}); it gets a value that matches.
            var path = System.Text.RegularExpressions.Regex.Replace(parts[1], @"\{(?:[^{}]|\{\{|\}\})+\}",
                m => m.Value.Contains(":regex(", StringComparison.Ordinal) ? new string('0', 32) : "1");
            using var request = new HttpRequestMessage(new(parts[0]), path) { Content = System.Net.Http.Json.JsonContent.Create(new { }) };
            var response = await anonymous.SendAsync(request);
            Assert.True(response.StatusCode == System.Net.HttpStatusCode.Unauthorized, route.Route + ": " + response.StatusCode);
        }
    }
    [Fact]
    public void The_api_exposes_exactly_the_documented_routes()
    {
        using var app = new TestApp();

        var actual = Routes(app).Select(r => r.Route).OrderBy(r => r, StringComparer.Ordinal).ToList();

        Assert.Equal(ExpectedRoutes.OrderBy(r => r, StringComparer.Ordinal).ToList(), actual);
    }

    [Fact]
    public void Every_route_requires_authorization()
    {
        using var app = new TestApp();

        var unprotected = Routes(app)
            .Where(r => r.Endpoint.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Select(r => r.Route)
            .ToList();

        Assert.Equal(AnonymousRoutes.Order(StringComparer.Ordinal), unprotected.Order(StringComparer.Ordinal));
        Assert.All(AnonymousRoutes, route => Assert.NotNull(Routes(app).Single(r => r.Route == route).Endpoint.Metadata.GetMetadata<IAllowAnonymous>()));
    }
}
