using System.Text.Json.Nodes;
using Endpoint = Construct.Companion.Core.Ipc.Endpoint;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Ipc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
namespace Construct.Companion.Host.Desktop;

// Standalone S2 UI activation only. S3 supplies the full IPC server and reuses
// IUiActivation. Never publishes endpoint.json: that advertises runtime ownership.
public sealed class DesktopActivationServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly IFileSystem files;
    private readonly string? endpointPath;
    public Endpoint Endpoint { get; }
    private DesktopActivationServer(WebApplication app, IFileSystem files, string? path, Endpoint endpoint)
    { this.app=app; this.files=files; endpointPath=path; Endpoint=endpoint; }
    public static async Task<DesktopActivationServer> StartAsync(IFileSystem files, string? endpointPath,
        IUiActivation ui, string version, CancellationToken cancellationToken = default, IMessageSink? messages = null)
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => { options.Listen(IPAddress.Loopback,0,l=>l.Protocols=HttpProtocols.Http1); options.Limits.MaxRequestBodySize=65536; });
        var app=builder.Build(); var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var started=DateTimeOffset.UtcNow; var pid=Environment.ProcessId;
        app.Use(async (context,next) =>
        {
            if (context.Request.Host.Host is not ("127.0.0.1" or "localhost") || context.Request.Host.Port != context.Connection.LocalPort)
            { context.Response.StatusCode=421; return; }
            if (context.Request.Path != "/v1/health")
            {
                var auth=context.Request.Headers.Authorization.ToString();
                var expected=Encoding.ASCII.GetBytes("Bearer "+token); var provided=Encoding.ASCII.GetBytes(auth);
                if (!CryptographicOperations.FixedTimeEquals(expected,provided)) { context.Response.StatusCode=401; return; }
            }
            await next(context);
        });
        app.MapGet("/v1/health",()=>Results.Json(new Health(true,version,pid,started),IpcJson.Options));
        app.MapPost("/v1/ui/activate",async (HttpContext context) =>
        {
            try
            {
                var activation=await context.Request.ReadFromJsonAsync<UiActivation>(IpcJson.Options,context.RequestAborted);
                if (activation is null || activation.View is not ("panel" or "settings" or "hostadmin" or "popup")) return Results.BadRequest();
                await ui.ActivateAsync([activation],context.RequestAborted);
                return Results.Json(new AcceptedResponse(),IpcJson.Options,statusCode:202);
            }
            catch (Exception e) when (e is JsonException or ArgumentException) { return Results.BadRequest(); }
        });
        app.MapPost("/v1/instances/{instance}/messages",async (HttpContext context,string instance) =>
        {
            if (messages is null) return Results.StatusCode(501);
            try
            {
                var message=await context.Request.ReadFromJsonAsync<JsonElement>(IpcJson.Options,context.RequestAborted);
                if (message.ValueKind!=JsonValueKind.Object) return Results.BadRequest();
                await messages.PostAsync(instance,message,context.RequestAborted);
                return Results.Json(new AcceptedResponse(),IpcJson.Options,statusCode:202);
            }
            catch (Exception e) when (e is JsonException or ArgumentException) { return Results.BadRequest(); }
        });
        app.MapPost("/v1/quit",(HttpContext context) =>
        {
            context.Response.OnCompleted(()=>ui.QuitAsync());
            return Results.Json(new AcceptedResponse(),IpcJson.Options,statusCode:202);
        });
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            var port=new Uri(app.Urls.Single()).Port;
            var endpoint=new Endpoint(1,port,token,pid,started,version);
            if (endpointPath is not null) files.WriteFileAtomic(endpointPath,JsonSerializer.SerializeToUtf8Bytes(endpoint,IpcJson.Options));
            return new(app,files,endpointPath,endpoint);
        }
        catch { await app.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        await app.StopAsync().ConfigureAwait(false); await app.DisposeAsync().ConfigureAwait(false);
        if (endpointPath is not null && files.ReadFile(endpointPath) is {} bytes)
        {
            try { if (JsonSerializer.Deserialize<Endpoint>(bytes,IpcJson.Options)?.Token == Endpoint.Token) files.DeleteFile(endpointPath); }
            catch (JsonException) { }
        }
    }
}
