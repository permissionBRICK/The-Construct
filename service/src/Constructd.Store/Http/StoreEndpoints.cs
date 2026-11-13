using Constructd.Store.Records;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Store.Http;

/// <summary>
/// The store's routes (9.2), the same in constructd (under <c>/api/v1/store</c>) and in the
/// stand-alone host. Who the caller is comes from <see cref="IStorePrincipalResolver"/>.
/// </summary>
public static class StoreEndpoints
{
    /// <summary>How long a feed request waits for a change at most.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    /// <param name="mutation">
    /// Called for each route that changes the store, with an action name (<c>store.write</c>,
    /// <c>store.delete</c>, <c>store.batch</c>, <c>store.import</c>); constructd marks them auditable.
    /// </param>
    public static IEndpointRouteBuilder MapRecordStore(this IEndpointRouteBuilder routes, Action<RouteHandlerBuilder, string>? mutation = null)
    {
        mutation ??= (_, _) => { };

        routes.MapGet("/records", async (HttpContext http, RecordStore store, long? since, int? wait, int? limit, CancellationToken ct) =>
        {
            if (Principal(http) is not { } principal) return Unauthorized(http);
            var waitFor = TimeSpan.FromSeconds(Math.Clamp(wait ?? 0, 0, (int)MaxWait.TotalSeconds));
            return Json(await store.FeedAsync(principal.Account, since ?? 0, waitFor, limit ?? 500, ct));
        });

        routes.MapGet("/records/{id}", async (HttpContext http, RecordStore store, string id, CancellationToken ct) =>
        {
            if (Principal(http) is not { } principal) return Unauthorized(http);
            return await store.GetAsync(principal.Account, id, ct) is { } record ? Json(record) : Error(404, "not-found", "No such record.");
        });

        routes.MapGet("/records/{id}/history", async (HttpContext http, RecordStore store, string id, int? limit, CancellationToken ct) =>
        {
            if (Principal(http) is not { } principal) return Unauthorized(http);
            return Json(await store.HistoryAsync(principal.Account, id, limit ?? RecordStore.HistoryVersions, ct));
        });

        mutation(routes.MapPut("/records/{id}", (HttpContext http, RecordStore store, string id, [FromBody] RecordWrite write, CancellationToken ct) =>
            WriteOneAsync(http, store, id, write, deletion: false, ct)), "store.write");

        mutation(routes.MapDelete("/records/{id}", (HttpContext http, RecordStore store, string id, [FromBody] RecordWrite write, CancellationToken ct) =>
            WriteOneAsync(http, store, id, write, deletion: true, ct)), "store.delete");

        mutation(routes.MapPost("/records/batch", async (HttpContext http, RecordStore store, [FromBody] BatchRequest batch, CancellationToken ct) =>
        {
            if (Principal(http) is not { } principal) return Unauthorized(http);
            return await store.BatchAsync(principal, batch.Writes ?? [], ct) switch
            {
                BatchOutcome.Accepted accepted => Json(accepted.Records),
                BatchOutcome.Stale stale => Json(stale.Conflict, StatusCodes.Status412PreconditionFailed),
                BatchOutcome.Refused refused => Error(refused.Status, refused.Code, refused.Message),
                _ => throw new InvalidOperationException(),
            };
        }), "store.batch");

        routes.MapGet("/epochs", async (HttpContext http, RecordStore store, CancellationToken ct) =>
        {
            if (Principal(http) is not { } principal) return Unauthorized(http);
            return Json(new { active = await store.ActiveEpochAsync(principal.Account, ct) });
        });

        routes.MapGet("/export", async (HttpContext http, RecordStore store, CancellationToken ct) =>
        {
            if (Principal(http) is not { Kind: ParticipantKind.Device } principal) return DevicesOnly(http);
            return Json(await store.ExportAsync(principal.Account, ct));
        });

        mutation(routes.MapPost("/import", async (HttpContext http, RecordStore store, [FromBody] StoreExport export, CancellationToken ct) =>
        {
            if (Principal(http) is not { Kind: ParticipantKind.Device } principal) return DevicesOnly(http);
            return await store.ImportAsync(principal.Account, export, ct)
                ? Results.NoContent()
                : Error(409, "store-not-empty", "An import goes into an account that has no records yet.");
        }), "store.import");

        return routes;
    }

    private static async Task<IResult> WriteOneAsync(HttpContext http, RecordStore store, string id, RecordWrite write, bool deletion, CancellationToken ct)
    {
        if (Principal(http) is not { } principal) return Unauthorized(http);
        if (!string.Equals(id, write.Id, StringComparison.Ordinal)) return Error(400, "id-mismatch", "The envelope's id is not the record in the path.");
        if (write.Deleted != deletion)
            return Error(400, deletion ? "not-a-deletion" : "use-delete", deletion ? "A DELETE carries deleted: true." : "A deletion is a DELETE.");
        if (IfMatch(http) is not { } ifMatch) return Error(428, "if-match-required", "A write carries If-Match: the revision it is based on, 0 for a new record.");
        if (ifMatch != write.BaseRevision) return Error(400, "base-mismatch", "If-Match is not the envelope's baseRevision.");

        return await store.WriteAsync(principal, write, ct) switch
        {
            WriteOutcome.Accepted accepted => Json(accepted.Record),
            WriteOutcome.Stale stale => Json(stale.Conflict, StatusCodes.Status412PreconditionFailed),
            WriteOutcome.Refused refused => Error(refused.Status, refused.Code, refused.Message),
            _ => throw new InvalidOperationException(),
        };
    }

    /// <summary><c>If-Match: 7</c> or <c>If-Match: "7"</c>.</summary>
    private static long? IfMatch(HttpContext http)
    {
        var raw = http.Request.Headers.IfMatch.ToString().Trim();
        if (raw.StartsWith("W/", StringComparison.Ordinal)) return null;
        return long.TryParse(raw.Trim('"'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static StorePrincipal? Principal(HttpContext http) =>
        http.RequestServices.GetRequiredService<IStorePrincipalResolver>().Resolve(http);

    private static IResult Unauthorized(HttpContext http) => http.User.Identity?.IsAuthenticated == true
        ? Error(403, "not-a-participant", "This caller is not a participant of the store.")
        : Error(401, "unauthenticated", "The store needs an authenticated caller.");

    private static IResult DevicesOnly(HttpContext http) => Principal(http) is null
        ? Unauthorized(http)
        : Error(403, "devices-only", "Only a device exports or imports the store.");

    private static IResult Json(object value, int status = StatusCodes.Status200OK) =>
        Results.Json(value, StoreJson.Options, statusCode: status);

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { code, message }, StoreJson.Options, statusCode: status);
}
