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
        "GET /api/v1/whoami",
        "POST /api/v1/users",
        "DELETE /api/v1/users/{name}",
        "POST /api/v1/users/{name}/tokens",
        "GET /api/v1/audit",
        "GET /api/v1/vms",
        "POST /api/v1/vms",
        "GET /api/v1/vms/{name}",
        "DELETE /api/v1/vms/{name}",
        "POST /api/v1/vms/{name}/power",
        "GET /api/v1/vms/{name}/state",
        "GET /api/v1/vms/{name}/endpoint",
        "GET /api/v1/vms/{name}/forwards",
        "POST /api/v1/vms/{name}/forwards",
        "DELETE /api/v1/vms/{name}/forwards/{id}",
        "GET /api/v1/vms/{name}/idle-policy",
        "PUT /api/v1/vms/{name}/idle-policy",
        "POST /api/v1/vms/{name}/activity",
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

        Assert.Empty(unprotected);
    }
}
