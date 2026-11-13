using System.Security.Claims;
using System.Text.Json;
using Constructd.Api.Auth;
using Constructd.Api.Infrastructure;
using Constructd.Core.Logic;
using Constructd.Core.Services;

namespace Constructd.Api.Endpoints;

/// <summary>
/// One change on <c>GET /events</c>: its sequence number (also the SSE <c>id</c>), what changed
/// (<c>vm.created</c>, <c>vm.changed</c>, <c>vm.power</c>, <c>vm.deleted</c>, <c>job.started</c>,
/// <c>job.progress</c>, <c>job.ended</c>, <c>backup.added</c>, <c>backup.removed</c>,
/// <c>update.offered</c>, <c>update.finished</c>, or <c>reset</c>) and the id of the thing (the VM's
/// name, the job's, backup's or update's id, a release commit for an offer), with the VM it belongs to.
/// </summary>
public sealed record ChangeEventResponse(long Seq, string Type, string? Id = null, string? Vm = null);

/// <summary>
/// The engine's change stream (design 6.1). A client follows it and re-reads what it shows; the
/// events name what changed, never the state. A caller hears only of what it may read: its own VMs,
/// backups and jobs (as owner or initiator), finished host updates; an admin hears of everything.
/// </summary>
public static class EventEndpoints
{
    public static RouteGroupBuilder MapEventEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/events", EventsAsync).RequireAuthorization(Policies.User).WithName("GetEvents")
            .Produces<ChangeEventResponse>(StatusCodes.Status200OK, "text/event-stream")
            .WithDescription("Server-sent events from `since` on (the `id` of the last event seen). Without `since`, or when the events after it are gone (too far behind, or an earlier run of the engine), the stream starts with `reset`: reload what you show, then continue from its `seq`.");
        return api;
    }

    /// <summary>The keepalive comment's interval.</summary>
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A stream ends after this long and the client resumes with its cursor, nothing lost: who may hear
    /// of what is judged at connect, so a revoked device or a demoted admin stops hearing within this time.
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private static async Task EventsAsync(HttpContext http, ChangeFeed feed, long? since)
    {
        var from = since ?? (long.TryParse(http.Request.Headers["Last-Event-ID"].ToString(), out var last) ? last : null);
        using var subscription = feed.Subscribe(from);
        var response = http.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        lifetime.CancelAfter(Lifetime);
        var ct = lifetime.Token;
        try
        {
            await response.WriteAsync(": connected\n\n", ct);
            if (from is null || subscription.Gap)
                await WriteAsync(response, new ChangeEventResponse(subscription.Current, "reset"), ct);
            foreach (var change in subscription.Replay.Where(c => Visible(http.User, c)))
                await WriteAsync(response, From(change), ct);
            await response.Body.FlushAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                using var keepalive = CancellationTokenSource.CreateLinkedTokenSource(ct);
                keepalive.CancelAfter(KeepAlive);
                try
                {
                    if (!await subscription.Live.WaitToReadAsync(keepalive.Token)) break;
                    while (subscription.Live.TryRead(out var change))
                        if (Visible(http.User, change)) await WriteAsync(response, From(change), ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await response.WriteAsync(": keepalive\n\n", ct);
                }

                await response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client went away, or the stream's lifetime ended: the client connects again with its cursor.
        }
    }

    /// <summary>The same rule as reading the thing itself: an admin, the owner, or a job's (user) initiator.</summary>
    public static bool Visible(ClaimsPrincipal user, Change change) =>
        user.IsAdmin() || change.Everyone || Ownership.SameName(user.NameOrEmpty(), change.Owner) ||
        change.Initiator?.StartsWith("vm:", StringComparison.OrdinalIgnoreCase) == false && Ownership.SameName(user.Actor(), change.Initiator);

    private static ChangeEventResponse From(Change change) => new(change.Seq, change.Type, change.Id, change.Vm);

    private static Task WriteAsync(HttpResponse response, ChangeEventResponse change, CancellationToken ct) =>
        response.WriteAsync($"id: {change.Seq}\nevent: {change.Type}\ndata: {JsonSerializer.Serialize(change, ApiJson.Options)}\n\n", ct);
}
