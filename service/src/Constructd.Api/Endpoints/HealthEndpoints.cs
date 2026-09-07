using Constructd.Api.Auth;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Api.Endpoints;

public static class HealthEndpoints
{
    public static RouteGroupBuilder MapHealthEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/health", async (HttpContext http, IReleaseInfo release, IMaintenanceGate gate, Constructd.Core.Configuration.ConstructdOptions options, IHostConfigStore config, CancellationToken ct) =>
        {
            var schema=release.SchemaVersion;
            var healthy=true;
            if(gate.State==MaintenanceState.Maintenance && options.EffectivePersistence==Constructd.Core.Configuration.PersistenceMode.Sqlite)
            {
                try { var db=await Constructd.Api.Admin.AdminDbCheck.CheckAsync(options.DatabasePath,ct); schema=db.SchemaVersion; healthy=db.Status=="ok"; }
                catch(Exception ex) when(ex is not OperationCanceledException) { healthy=false; }
            }
            var marker=gate.State==MaintenanceState.Open ? null : await config.GetAsync<MaintenanceMarker>("maintenance",ct);
            var body = new Dictionary<string, object?>
            {
                ["status"] = !healthy ? "failed" : gate.State == MaintenanceState.Open ? "ok" : "maintenance",
                ["schemaVersion"] = schema,
                ["schemaMinReadableBy"] = release.SchemaMinReadableBy,
                ["apiFeatures"] = release.ApiFeatures,
                ["maintenance"] = gate.State == MaintenanceState.Open ? null :
                    new { phase = gate.State, since=marker?.Since, updateId=marker?.UpdateId, retryAfterSeconds = 30 }
            };
            if (http.User.Identity?.IsAuthenticated == true && (http.User.IsKnownUser() || http.User.IsVmToken() || http.User.Identity.AuthenticationType == UpdateHandoffAuthenticationHandler.SchemeName))
            { body["commit"] = release.Installed.Commit; body["packageVersion"] = release.Installed.PackageVersion; body["installedAt"] = release.Installed.InstalledAt; }
            return Results.Ok(body);
        }).AllowAnonymous().WithName("Health");
        return api;
    }
}
