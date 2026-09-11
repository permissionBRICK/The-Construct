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
        "POST /api/v1/media/acquire",
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
        "GET /api/v1/health",
        "GET /api/v1/host/status",
        "GET /api/v1/host/iso-catalog",
        "GET /api/v1/host/capacity",
        "GET /api/v1/host/capabilities",
        "GET /api/v1/host/config",
        "PUT /api/v1/host/config",
        "GET /api/v1/users",
        "GET /api/v1/users/{name}",
        "PUT /api/v1/users/{name}",
        "GET /api/v1/users/{name}/allowance",
        "PUT /api/v1/users/{name}/allowance",
        "GET /api/v1/users/{name}/tokens",
        "DELETE /api/v1/users/{name}/tokens/{id}",
        "GET /api/v1/vms/{name}/identity",
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
        "PUT /api/v1/vms/{name}/cpu",
        "PUT /api/v1/vms/{name}/memory",
        "POST /api/v1/vms",
        "GET /api/v1/vms/{name}",
        "DELETE /api/v1/vms/{name}",
        "POST /api/v1/vms/{name}/power",
        "POST /api/v1/vms/{parent}/children",
        "GET /api/v1/vms/{name}/state",
        "GET /api/v1/vms/{name}/endpoint",
        "GET /api/v1/vms/{name}/forwards",
        "POST /api/v1/vms/{name}/forwards",
        "DELETE /api/v1/vms/{name}/forwards/{id}",
        "POST /api/v1/vms/{name}/forwards/{id}/ack",
        "GET /api/v1/vms/{name}/idle-policy",
        "PUT /api/v1/vms/{name}/idle-policy",
        "POST /api/v1/vms/{name}/activity",
        "GET /api/v1/jobs",
        "POST /api/v1/jobs/{id}/cancel",
        "POST /api/v1/vms/{name}/lifecycle",
        "POST /api/v1/vms/{name}/lease",
        "PUT /api/v1/vms/{name}/sharing",
        "PUT /api/v1/vms/{name}/hardware",
        "PUT /api/v1/vms/{name}/media",
        "GET /api/v1/jobs/{id}",
        "GET /api/v1/jobs/{id}/events",
    ];

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
        foreach (var route in Routes(app).Where(r => r.Route != "GET /api/v1/health"))
        {
            var parts = route.Route.Split(' ', 2);
            var path = System.Text.RegularExpressions.Regex.Replace(parts[1], @"\{[^}]+\}", "1");
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

        Assert.Equal(["GET /api/v1/health"], unprotected);
        Assert.NotNull(Routes(app).Single(r => r.Route == "GET /api/v1/health").Endpoint.Metadata.GetMetadata<IAllowAnonymous>());
    }
}
