using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Ipc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Construct.Companion.Host.Ipc;

public sealed record IpcServerOptions(string Version = "development", bool PublishEndpoint = true);
public static class IpcServer
{
    // The application owns this host and awaits StopAsync/DisposeAsync on exit.
    public static WebApplication Build(Action<IServiceCollection> configure, IpcServerOptions? options = null)
    {
        options ??= new();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders(); // URLs, bearer headers and request bodies never enter ASP.NET logs.
        builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1); k.Limits.MaxRequestBodySize = 1024 * 1024; });
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(4));
        configure(builder.Services);
        var app = builder.Build();
        var files = app.Services.GetRequiredService<IFileSystem>();
        var clock = app.Services.GetRequiredService<IClock>();
        var settings = app.Services.GetRequiredService<IpcSettings>();
        var backend = app.Services.GetRequiredService<CompanionBackend>();
        var events = app.Services.GetRequiredService<IpcEvents>();
        var logs = app.Services.GetRequiredService<IpcLogs>();
        var desktop = app.Services.GetRequiredService<ICompanionDesktop>();
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var startedAt = clock.UtcNow; var version = options.Version; var pid = Environment.ProcessId;
        var endpointPath = Path.Combine(settings.Directory, "endpoint.json");
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (!options.PublishEndpoint) return;
            var port = new Uri(app.Urls.Single()).Port;
            files.CreateDirectory(settings.Directory);
            files.WriteFileAtomic(endpointPath, JsonSerializer.SerializeToUtf8Bytes(new Core.Ipc.Endpoint(1, port, secret, pid, startedAt, version), IpcJson.Options));
            logs.Write("IPC listener started.");
        });
        app.Lifetime.ApplicationStopped.Register(() => { if (options.PublishEndpoint) { files.DeleteFile(endpointPath); logs.Write("IPC listener stopped."); } });
        app.Use(async (ctx, next) =>
        {
            try
            {
                var host = ctx.Request.Host;
                if (host.Host is not ("127.0.0.1" or "localhost") || host.Port != ctx.Connection.LocalPort)
                    throw new IpcFailure(421, "misdirectedRequest", "Unrecognized Host header.");
                if (!(HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Path == "/v1/health"))
                {
                    var header = ctx.Request.Headers.Authorization.ToString();
                    var expected = Encoding.ASCII.GetBytes("Bearer " + secret);
                    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header), expected))
                        throw new IpcFailure(401, "unauthorized", "A valid bearer token is required.");
                }
                await next(ctx);
                if (ctx.Response.StatusCode is 404 or 405 && !ctx.Response.HasStarted)
                    await ProblemAsync(ctx, ctx.Response.StatusCode, "routeNotFound", "No matching route.");
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
            catch (IpcFailure e) when (!ctx.Response.HasStarted) { await ProblemAsync(ctx, e.Status, e.Code, e.Message); }
            catch (Exception e) when (!ctx.Response.HasStarted)
            {
                logs.Failure("IPC request", e);
                var malformed = e is JsonException or Microsoft.AspNetCore.Http.BadHttpRequestException;
                await ProblemAsync(ctx, malformed ? 400 : 500, malformed ? "invalidRequest" : "operationFailed", malformed ? "A JSON object is required." : "The operation failed. See Companion diagnostics.");
            }
        });
        app.MapGet("/v1/health", () => Json(new Health(true, version, pid, startedAt)));
        app.MapGet("/v1/state", async (CancellationToken ct) => Json(await backend.StateAsync(ct)));
        app.MapGet("/v1/instances/{name}/snapshot", async (string name, CancellationToken ct) => Json(await backend.SnapshotAsync(name, ct)));
        app.MapPost("/v1/instances/{name}/messages", async (string name, HttpContext c) => { await backend.DispatchAsync(name, await Body(c), c.RequestAborted); return Accepted(); });
        app.MapPost("/v1/instances/{name}/select", async (string name, CancellationToken ct) => { await backend.SelectAsync(name, ct); return Accepted(); });
        app.MapGet("/v1/hosts", async (CancellationToken ct) => Json(await backend.HostsAsync(ct)));
        app.MapPost("/v1/hosts", async (HttpContext c) => { var body = (await Body(c)).Deserialize<AddRemoteHost>(IpcJson.Options); if (body is null || string.IsNullOrWhiteSpace(body.Url)) throw new IpcFailure(400, "invalidHost", "A host URL is required."); return Json(await backend.AddHostAsync(body, c.RequestAborted), 201); });
        app.MapDelete("/v1/hosts/{slug}", async (string slug, CancellationToken ct) => { await backend.RemoveHostAsync(slug, ct); return Accepted(); });
        app.MapGet("/v1/hosts/{slug}/snapshot", async (string slug, CancellationToken ct) => Json(await backend.HostSnapshotAsync(slug, ct)));
        app.MapPost("/v1/hosts/{slug}/messages", async (string slug, HttpContext c) => { await backend.HostDispatchAsync(slug, await Body(c), c.RequestAborted); return Accepted(); });
        app.MapGet("/v1/settings", () => Json(settings.Read()));
        app.MapPut("/v1/settings", async (HttpContext c) =>
        {
            var patch = await Body(c);
            if (patch["activeInstance"] is JsonValue active && active.TryGetValue<string>(out var name)) await backend.SnapshotAsync(name, c.RequestAborted);
            return Json(settings.Merge(patch));
        });
        app.MapPost("/v1/ui/activate", async (HttpContext c) =>
        {
            var activation = (await Body(c)).Deserialize<UiActivation>(IpcJson.Options);
            if (activation?.View is not ("panel" or "settings" or "hostadmin" or "popup")) throw new IpcFailure(400, "invalidView", "Unknown Companion view.");
            if (activation.Instance is not null) await backend.SnapshotAsync(activation.Instance, c.RequestAborted);
            if (activation.Host is not null) await backend.HostSnapshotAsync(activation.Host, c.RequestAborted);
            await desktop.ActivateAsync(activation, c.RequestAborted); return Accepted();
        });
        app.MapPost("/v1/quit", async (HttpContext c) =>
        {
            if ((await Body(c))["reason"]?.GetValue<string>() is not ("update" or "user")) throw new IpcFailure(400, "invalidReason", "Quit reason must be update or user.");
            c.Response.OnCompleted(() => { app.Lifetime.StopApplication(); return Task.CompletedTask; }); return Accepted();
        });
        app.MapGet("/v1/logs", (HttpContext c) =>
        { var raw = c.Request.Query["lines"].ToString(); if (raw.Length > 0 && (!int.TryParse(raw, out _) || int.Parse(raw) < 0)) throw new IpcFailure(400, "invalidLines", "lines must be a nonnegative integer."); return Json(logs.Read(raw.Length == 0 ? 200 : int.Parse(raw))); });
        app.MapGet("/v1/events", async (HttpContext c) =>
        {
            var instance = c.Request.Query["instance"].ToString();
            if (instance.Length > 0) await backend.SnapshotAsync(instance, c.RequestAborted);
            using var subscription = events.Subscribe(instance.Length == 0 ? null : instance);
            c.Response.ContentType = "text/event-stream"; c.Response.Headers.CacheControl = "no-cache";
            await c.Response.WriteAsync(": connected\n\n", c.RequestAborted); await c.Response.Body.FlushAsync(c.RequestAborted);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted, app.Lifetime.ApplicationStopping);
            try
            {
                var ready = subscription.Reader.WaitToReadAsync(stop.Token).AsTask();
                var keepalive = clock.DelayAsync(TimeSpan.FromSeconds(15), stop.Token);
                while (!stop.IsCancellationRequested)
                {
                    var completed = await Task.WhenAny(ready, keepalive);
                    if (completed == keepalive)
                    { await keepalive; await c.Response.WriteAsync(": keepalive\n\n", stop.Token); keepalive = clock.DelayAsync(TimeSpan.FromSeconds(15), stop.Token); }
                    else
                    {
                        if (!await ready) break;
                        while (subscription.Reader.TryRead(out var item)) await c.Response.WriteAsync($"event: {item.Event}\ndata: {item.Data.GetRawText()}\n\n", stop.Token);
                        ready = subscription.Reader.WaitToReadAsync(stop.Token).AsTask();
                    }
                    await c.Response.Body.FlushAsync(stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally { await stop.CancelAsync(); }
        });
        return app;
    }
    private static IResult Json(object? value, int status = 200) => Results.Json(value, IpcJson.Options, statusCode: status);
    private static IResult Accepted() => Json(new AcceptedResponse(), 202);
    private static async Task<JsonObject> Body(HttpContext c) => await JsonNode.ParseAsync(c.Request.Body, cancellationToken: c.RequestAborted) as JsonObject ?? throw new IpcFailure(400, "invalidRequest", "A JSON object is required.");
    private static async Task ProblemAsync(HttpContext c, int status, string code, string title)
    { c.Response.StatusCode = status; c.Response.ContentType = "application/problem+json"; await c.Response.WriteAsync(JsonSerializer.Serialize(new Problem("about:blank", title, status, code), IpcJson.Options), c.RequestAborted); }
}
