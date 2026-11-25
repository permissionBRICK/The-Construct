using Construct.Agent.Upstream;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Construct.Agent.Http;

/// <summary>
/// Upstream config repos (design 9.7) under <c>/api/upstream</c>: what the last refresh found, refreshing now,
/// linking/unlinking repositories, listing their profile files and importing selected files. A refresh or change
/// arrives as an <c>upstream</c> event.
/// </summary>
public static class UpstreamRoutes
{
    public static void Map(RouteGroupBuilder api, UpstreamSync upstream)
    {
        api.MapGet("/upstream", async (CancellationToken ct) => TypedResults.Ok(await upstream.ViewAsync(ct))).WithName("GetUpstream")
            .WithDescription("The linked upstream config repos as the last refresh found them: per repository the commit read, per tracked file whether it is up to date, merged, in conflict, removed upstream or refused.");

        api.MapPost("/upstream/refresh", async (CancellationToken ct) => TypedResults.Ok(await upstream.RefreshAsync(ct))).WithName("RefreshUpstream")
            .WithDescription("Fetches every linked upstream config repo now with this PC's git credentials and hands each tracked file's new version to the store's engine, which merges it into its profile. Answers when done.");

        api.MapPost("/upstream/repos", async ([FromBody] AddUpstreamRepoRequest? request, CancellationToken ct) =>
            TypedResults.Ok(await upstream.AddRepoAsync(request?.Url, request?.Ref, ct))).WithName("AddUpstreamRepo")
            .WithDescription("Links an upstream config repo by URL (without a password) and optional branch/ref, fetches it with this PC's git credentials and returns the profile files found in it.");

        api.MapGet("/upstream/repos/{id}/files", async (string id, CancellationToken ct) =>
            TypedResults.Ok(await upstream.ListRepoFilesAsync(id, ct))).WithName("ListUpstreamRepoFiles")
            .WithDescription("Fetches a linked upstream config repo and returns the profile files found in it, marking which are already tracked or collide with an existing local profile.");

        api.MapPost("/upstream/repos/{id}/import", async (string id, [FromBody] ImportUpstreamRequest? request, CancellationToken ct) =>
            TypedResults.Ok(await upstream.ImportAsync(id, request?.Files, ct))).WithName("ImportUpstreamFiles")
            .WithDescription("Imports selected profile files from a linked upstream config repo into the account through the store's engine and starts tracking them for future refreshes.");

        api.MapDelete("/upstream/repos/{id}", async (string id, CancellationToken ct) =>
        {
            await upstream.RemoveRepoAsync(id, ct);
            return TypedResults.NoContent();
        }).WithName("RemoveUpstreamRepo")
            .WithDescription("Unlinks an upstream config repo and clears its local cache. Already-imported profiles stay in the account.");
    }
}

public sealed record AddUpstreamRepoRequest(string? Url, string? Ref = null);

public sealed record ImportUpstreamRequest(IReadOnlyList<ImportUpstreamFile>? Files);
