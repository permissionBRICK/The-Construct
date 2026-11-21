using Construct.Agent.Upstream;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Construct.Agent.Http;

/// <summary>
/// Upstream config repos (design 9.7) under <c>/api/upstream</c>: what the last refresh found, and a refresh now. A
/// refresh's start and end arrive as <c>upstream</c> events.
/// </summary>
public static class UpstreamRoutes
{
    public static void Map(RouteGroupBuilder api, UpstreamSync upstream)
    {
        api.MapGet("/upstream", () => TypedResults.Ok(upstream.View())).WithName("GetUpstream")
            .WithDescription("The linked upstream config repos as the last refresh found them: per repository the commit read, per tracked file whether it is up to date, merged, in conflict, removed upstream or refused.");

        api.MapPost("/upstream/refresh", async (CancellationToken ct) => TypedResults.Ok(await upstream.RefreshAsync(ct))).WithName("RefreshUpstream")
            .WithDescription("Fetches every linked upstream config repo now with this PC's git credentials and hands each tracked file's new version to the store's engine, which merges it into its profile. Answers when done.");
    }
}
