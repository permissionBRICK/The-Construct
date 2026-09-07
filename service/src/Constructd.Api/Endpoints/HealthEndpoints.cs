using Constructd.Api.Auth;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Api.Endpoints;

public static class HealthEndpoints
{
    public static RouteGroupBuilder MapHealthEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/health", (HttpContext http, IReleaseInfo release, IMaintenanceGate gate) =>
        {
            var body = new Dictionary<string, object?>
            {
                ["status"] = gate.State == MaintenanceState.Open ? "ok" : "maintenance",
                ["schemaVersion"] = release.SchemaVersion,
                ["schemaMinReadableBy"] = release.SchemaMinReadableBy,
                ["apiFeatures"] = release.ApiFeatures,
                ["maintenance"] = gate.State == MaintenanceState.Open ? null :
                    new { phase = gate.State, retryAfterSeconds = 30 }
            };
            if (http.User.Identity?.IsAuthenticated == true)
            { body["commit"] = release.Installed.Commit; body["packageVersion"] = release.Installed.PackageVersion; body["installedAt"] = release.Installed.InstalledAt; }
            return Results.Ok(body);
        }).AllowAnonymous().WithName("Health");
        return api;
    }
}
