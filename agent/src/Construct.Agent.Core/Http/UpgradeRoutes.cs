using Construct.Agent.Upgrade;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Construct.Agent.Http;

/// <summary>
/// The one-time upgrade dialog's routes under <c>/api/upgrade</c> (11.6, 13.2). The recovery-code step is the
/// existing <c>POST /api/device/setup</c> against <c>storeEngine</c>; an upgrade's setup keeps the old vault key.
/// Progress arrives as <c>upgrade</c> events; <c>GET /api/upgrade</c> is the whole state.
/// </summary>
public static class UpgradeRoutes
{
    public static void Map(RouteGroupBuilder api, UpgradeService upgrade)
    {
        api.MapGet("/upgrade", () => TypedResults.Ok(upgrade.View())).WithName("GetUpgrade")
            .WithDescription("Where the one-time upgrade stands: the steps (engine, recovery, close, convert), what the old install holds, the last run's items.");

        api.MapPost("/upgrade/engine", () => TypedResults.Accepted("/api/upgrade", upgrade.StartEngine())).WithName("StartUpgradeEngine")
            .WithDescription("Step 1: installs this PC's engine with one elevation prompt when it has a local VM, and adds the shared hosts with their pins and sign-ins. Repeatable.");

        api.MapGet("/upgrade/writers", () => TypedResults.Ok(new OldWritersView(upgrade.OldWriters()))).WithName("GetUpgradeWriters")
            .WithDescription("Step 3: the old writers still open (VS Code, the old Companion); the conversion waits until the list is empty.");

        api.MapPost("/upgrade/convert", ([FromBody] ConvertRequest? request) => TypedResults.Accepted("/api/upgrade", upgrade.StartConvert(request ?? new ConvertRequest())))
            .WithName("StartUpgradeConvert")
            .WithDescription("Step 4: copies the old state into the new stack and switches the local VMs over; repeatable, a second run changes nothing already there.");
    }
}

public sealed record OldWritersView(IReadOnlyList<string> Open);
