using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Construct.Agent.Http;

/// <summary>
/// <c>/api/engines/{id}/api/v1/...</c> → that engine's <c>/api/v1/...</c> (6.2). The request goes over
/// the engine's pinned connection with this device's token and a fresh proof (or the user's sign-in
/// before the device is enrolled there). Nothing the UI sent as a credential passes: only the body and
/// a short list of content headers do. What the engine changes reaches <c>/api/events</c> through
/// its change stream (<see cref="Events.EngineStreams"/>), not through the proxy.
/// </summary>
public static class EngineProxy
{
    private static readonly string[] ForwardedRequestHeaders = ["Accept", "Accept-Language", "If-Match", "If-None-Match", "Last-Event-ID"];

    private static readonly HashSet<string> DroppedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Authenticate", "Trailer",
        "Set-Cookie", "WWW-Authenticate", "Strict-Transport-Security", "Alt-Svc", "Server", "Access-Control-Allow-Origin",
        "Access-Control-Allow-Credentials", "Access-Control-Allow-Headers", "Access-Control-Allow-Methods",
    };

    public static void Map(RouteGroupBuilder api) =>
        api.MapMethods("/engines/{id}/api/v1/{**rest}", ["GET", "POST", "PUT", "PATCH", "DELETE"], ProxyAsync).ExcludeFromDescription();

    private static async Task ProxyAsync(HttpContext context, DeviceAgent agent, string id)
    {
        if (agent.ProxyClient(id) is not { } client) throw new AgentFailure(404, "no-engine", "No such engine.");

        // The raw target keeps the path's escaping as the UI wrote it (a record id with %3A, for one).
        var raw = context.Features.Get<IHttpRequestFeature>()!.RawTarget;
        var prefix = $"/api/engines/{id}";
        if (!raw.StartsWith(prefix + "/api/v1/", StringComparison.Ordinal)) throw new AgentFailure(404, "not-found", "Engine calls are /api/engines/{id}/api/v1/....");
        var target = raw[(prefix.Length + 1)..];
        if (target.Split('?')[0].Split('/').Any(segment => segment is "." or ".." || segment.Contains('\\') || Uri.UnescapeDataString(segment) is "." or ".."))
            throw new AgentFailure(400, "invalid-path", "The path leaves the engine's API.");

        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), new Uri(target, UriKind.Relative));
        if (context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0)
        {
            request.Content = new StreamContent(context.Request.Body);
            // A fixed length stays fixed: some engine routes (media chunks) require Content-Length.
            if (context.Request.ContentLength is { } length) request.Content.Headers.ContentLength = length;
            if (context.Request.ContentType is { } contentType) request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        foreach (var name in ForwardedRequestHeaders)
        {
            if (context.Request.Headers.TryGetValue(name, out var values)) request.Headers.TryAddWithoutValidation(name, values.ToArray());
        }

        agent.Stamp(id, request.Headers);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        }
        catch (HttpRequestException ex)
        {
            // A certificate other than the pinned one ends here as well.
            throw new AgentFailure(502, "engine-unreachable", "The engine did not answer over its pinned connection: " + ex.Message);
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (DroppedResponseHeaders.Contains(header.Key)) continue;
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            if (response.Headers.Location is { } location)
            {
                var path = location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
                if (path.StartsWith("/api/v1/", StringComparison.Ordinal)) context.Response.Headers.Location = prefix + path;
                else context.Response.Headers.Remove("Location");
            }

            await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                // Event streams reach the UI as they come.
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
    }
}
