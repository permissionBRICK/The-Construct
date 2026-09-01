using Constructd.Api.Auth;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Api.Infrastructure;

/// <summary>
/// Marks a route as auditable and says what to call the action and where to find its target.
/// </summary>
/// <param name="TargetRouteValue">Route parameter holding the target, when the body does not name it.</param>
public sealed record AuditActionMetadata(string Action, string TargetRouteValue);

/// <summary>
/// One middleware owns the outcome of every request: it audits it, and it is where a failure stops.
///
/// It is the OUTERMOST middleware, so nothing in the pipeline can fail past it — routing, the
/// authentication handlers (which is where a presented plaintext token is in scope), authorization,
/// model binding, the endpoint and its dependencies. It reads the route's audit metadata after
/// <c>next</c> unwinds, by which time routing has resolved the endpoint and authentication has set the
/// identity, so the entry still says who did what:
///
/// <list type="bullet">
/// <item>an authorization refusal (401/403) that never reaches the handler,</item>
/// <item>a model-binding failure such as malformed JSON, which happens before the handler runs,</item>
/// <item>the handler's own answer, including its validation errors and conflicts,</item>
/// <item>an exception from any dependency — a token service, a driver, a forward manager, a store —
/// which would otherwise leave no trace at all.</item>
/// </list>
///
/// It handles those exceptions itself instead of rethrowing, and the service registers no framework
/// exception handler above it, because that handler logs the exception <em>object</em>: its rendered
/// form carries the message, stack trace and <c>Data</c> — a command line with a seed password, or the
/// token a failing validation was looking up. Only <see cref="SafeError.Describe"/> of it is logged,
/// audited and returned.
///
/// Handlers contribute context with <see cref="AuditContext.SetAuditDetail"/>; they never write the
/// entry, so nothing can double-record or forget. Job bodies and the idle engine audit separately:
/// they are not requests.
/// </summary>
public sealed class RequestOutcomeMiddleware(RequestDelegate next, ILogger<RequestOutcomeMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await HandleAsync(context, ex).ConfigureAwait(false);
            return;
        }

        if (MetadataOf(context) is { } metadata)
        {
            await AuditContext.WriteAsync(context, metadata, context.Response.StatusCode).ConfigureAwait(false);
        }
    }

    private static AuditActionMetadata? MetadataOf(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<AuditActionMetadata>();

    private async Task HandleAsync(HttpContext context, Exception exception)
    {
        var safe = SafeError.Describe(exception);
        var status = exception is BadHttpRequestException badRequest
            ? badRequest.StatusCode
            : StatusCodes.Status500InternalServerError;

        // Safe description only — no exception object, in this log call or any other.
        logger.LogError("{Method} {Path} failed: {Error}.", context.Request.Method, context.Request.Path, safe);

        try
        {
            if (MetadataOf(context) is { } metadata)
            {
                context.AppendAuditDetail($"exception={safe}");
                await AuditContext.WriteAsync(context, metadata, status).ConfigureAwait(false);
            }

            if (context.Response.HasStarted)
            {
                // Mid-response (an SSE stream, say): there is nothing left to answer with.
                context.Abort();
                return;
            }

            context.Response.Clear();
            await Results
                .Problem(detail: $"The request failed: {safe}", statusCode: status, title: "Request failed")
                .ExecuteAsync(context)
                .ConfigureAwait(false);
        }
        catch (Exception secondary)
        {
            // Nothing may escape this middleware: an exception from here would land in the server's own
            // logger, complete with the original exception as its inner one.
            logger.LogError("Failed to report the failure of {Path}: {Error}.",
                context.Request.Path, SafeError.Describe(secondary));
            context.Abort();
        }
    }
}

/// <summary>Per-request audit context: what the handler wants recorded alongside the outcome.</summary>
public static class AuditContext
{
    private const string DetailKey = "constructd:audit-detail";
    private const string TargetKey = "constructd:audit-target";

    /// <summary>Sets the detail of this request's audit entry. Never pass a secret.</summary>
    public static void SetAuditDetail(this HttpContext http, string detail) => http.Items[DetailKey] = detail;

    /// <summary>Adds to the detail of this request's audit entry.</summary>
    public static void AppendAuditDetail(this HttpContext http, string detail) =>
        http.Items[DetailKey] = DetailOf(http) is { } existing ? $"{existing}, {detail}" : detail;

    /// <summary>Overrides the audit target (for routes whose target comes from the body).</summary>
    public static void SetAuditTarget(this HttpContext http, string target) => http.Items[TargetKey] = target;

    internal static Task WriteAsync(HttpContext http, AuditActionMetadata metadata, int status)
    {
        var audit = http.RequestServices.GetRequiredService<IAuditLog>();
        var clock = http.RequestServices.GetRequiredService<IClock>();

        var outcome = status switch
        {
            < 400 => AuditOutcome.Success,
            StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden => AuditOutcome.Denied,
            _ => AuditOutcome.Failure,
        };

        var target = http.Items.TryGetValue(TargetKey, out var explicitTarget) && explicitTarget is string name
            ? name
            : http.Request.RouteValues.TryGetValue(metadata.TargetRouteValue, out var routeValue)
                ? routeValue?.ToString() ?? "-"
                : "-";

        var detail = DetailOf(http) is { } handlerDetail
            ? $"status={status}, {handlerDetail}"
            : $"status={status}";

        return audit.AppendAsync(
            new AuditEntry(clock.UtcNow, http.User.Actor(), metadata.Action, target, outcome, detail),
            // The client may already be gone; the record must be written regardless.
            CancellationToken.None);
    }

    private static string? DetailOf(HttpContext http) =>
        http.Items.TryGetValue(DetailKey, out var detail) && detail is string text ? text : null;
}

/// <summary>Route-builder sugar for the above.</summary>
public static class AuditRouteExtensions
{
    /// <summary>
    /// Marks this route auditable: <see cref="RequestOutcomeMiddleware"/> then records exactly one
    /// entry per request to it. A test asserts that every mutating route carries this.
    /// </summary>
    public static RouteHandlerBuilder Audited(
        this RouteHandlerBuilder builder,
        string action,
        string targetRouteValue = "name") =>
        builder.WithMetadata(new AuditActionMetadata(action, targetRouteValue));
}
