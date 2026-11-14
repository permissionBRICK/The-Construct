using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Construct.Agent.Device;

namespace Construct.Agent.Http;

/// <summary>
/// The agent's local API (6.2) on loopback, in the style of the Companion's IPC server: a new token
/// per run in the token file, every <c>/api</c> route but <c>/api/health</c> requires it as a bearer
/// token, and the built UI is served from the same origin. A web page elsewhere cannot use it: it
/// does not know the token, a cross-origin request carrying an Authorization header needs a CORS
/// preflight this server never grants, any request whose Origin is not this agent is refused, and
/// a Host header other than this loopback address (DNS rebinding) is refused.
/// </summary>
public sealed class AgentHost : IAsyncDisposable
{
    private AgentHost(WebApplication app, DeviceAgent agent, AgentOptions options, string token, DeviceServices device, Upgrade.UpgradeService upgrade)
    {
        Upgrade = upgrade;
        App = app;
        Agent = agent;
        Options = options;
        Token = token;
        Device = device;
    }

    /// <summary>The one-time upgrade from the old client (11.6, 13.2).</summary>
    public Upgrade.UpgradeService Upgrade { get; }

    /// <summary>The device features: VM links, open actions, SSH configuration, updates.</summary>
    public DeviceServices Device { get; }

    public WebApplication App { get; }

    public DeviceAgent Agent { get; }

    public AgentOptions Options { get; }

    /// <summary>This run's API token (also in the token file).</summary>
    public string Token { get; }

    public int Port => new Uri(App.Urls.First()).Port;

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <param name="device">Builds the device features (tests pass fakes); by default from <see cref="AgentOptions.Platform"/>.</param>
    public static AgentHost Create(AgentOptions options, DeviceAgent? agent = null, Func<DeviceAgent, Action, DeviceServices>? device = null)
    {
        agent ??= new DeviceAgent(options);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        // URLs, tokens and bodies never enter a log.
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, options.Port, listen => listen.Protocols = HttpProtocols.Http1);
            kestrel.AddServerHeader = false;
        });
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(4));
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        builder.Services.AddSingleton(agent);
        builder.Services.AddSingleton(options);
        builder.Services.AddAgentOpenApi();

        var app = builder.Build();
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var stop = () => app.Lifetime.StopApplication();
        var services = device?.Invoke(agent, stop) ?? DeviceServices.Create(agent, options, options.Platform ?? new LinuxPlatform(), stop);
        var upgrade = new Upgrade.UpgradeService(agent, options,
            options.Upgrade ?? new Upgrade.DefaultUpgradePlatform(Path.Combine(options.DataDirectory, "legacy_known_hosts")), services.Settings, new AgentEngines(agent));
        var host = new AgentHost(app, agent, options, token, services, upgrade);
        var startedAt = options.Time.GetUtcNow();

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (options.PublishEndpoint) new EndpointFile(1, host.Port, token, Environment.ProcessId, startedAt, options.Version).Write(options.EndpointPath);
            agent.Streams.Sync();
        });
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            if (!options.PublishEndpoint) return;
            // Only this run's file: a newer agent may have written its own already.
            if (EndpointFile.Read(options.EndpointPath)?.Token == token) File.Delete(options.EndpointPath);
        });

        app.Use((context, next) => GuardAsync(context, next, host));
        if (options.UiDirectory is { } ui && Directory.Exists(ui))
        {
            var files = new PhysicalFileProvider(Path.GetFullPath(ui));
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        }
        else
        {
            app.MapGet("/", () => Results.Text($"The Construct agent is running, but its UI bundle was not found (looked in {options.UiDirectory ?? "no directory"}).")).ExcludeFromDescription();
        }

        AgentRoutes.Map(app, host, startedAt);
        DeviceRoutes.Map(app.MapGroup("/api").WithTags("device"), services);
        UpgradeRoutes.Map(app.MapGroup("/api").WithTags("upgrade"), upgrade);
        return host;
    }

    /// <summary>Starts the API, then the device features (VM links, update checks).</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await App.StartAsync(ct).ConfigureAwait(false);
        Device.Start();
    }

    /// <summary>
    /// The self-test at start (12.2): the API answers its own health route from this process. A new
    /// version that fails it is replaced by the previous one (<see cref="Update.AgentUpdater.StartupAsync"/>).
    /// </summary>
    public async Task SelfTestAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var health = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<AgentHealth>(http, BaseUrl + "/api/health", AgentJson.Options, ct).ConfigureAwait(false);
        if (health?.Pid != Environment.ProcessId || health.Status != "ok") throw new InvalidOperationException("The agent's health route did not answer from this process.");
    }

    public async ValueTask DisposeAsync()
    {
        await Device.DisposeAsync().ConfigureAwait(false);
        Upgrade.Dispose();
        await App.StopAsync().ConfigureAwait(false);
        await App.DisposeAsync().ConfigureAwait(false);
        Agent.Dispose();
    }

    private static async Task GuardAsync(HttpContext context, RequestDelegate next, AgentHost host)
    {
        var headers = context.Response.Headers;
        // Only VS Code may frame the page (the thin extension's view): its webview and the window around it.
        // A web page cannot. The framed page still needs the token, which only a local reader of the agent's file has.
        headers["Content-Security-Policy"] = "frame-ancestors vscode-webview: vscode-file:";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";

        var port = context.Connection.LocalPort;
        var requestHost = context.Request.Host;
        if (requestHost.Host is not ("127.0.0.1" or "localhost") || requestHost.Port != port)
        {
            await ProblemAsync(context, 421, "misdirected-request", "This agent answers on its loopback address only.");
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && origin != $"http://127.0.0.1:{port}" && origin != $"http://localhost:{port}")
        {
            await ProblemAsync(context, 403, "foreign-origin", "Requests from other origins are refused.");
            return;
        }

        var path = context.Request.Path;
        if (path.StartsWithSegments("/api"))
        {
            headers.CacheControl = "no-store";
            if (!(HttpMethods.IsGet(context.Request.Method) && path == "/api/health") && !Authorized(context, host.Token))
            {
                await ProblemAsync(context, 401, "unauthorized", "The agent's token is required (the token file).");
                return;
            }
        }

        try
        {
            await next(context);
        }
        catch (AgentFailure failure) when (!context.Response.HasStarted)
        {
            await ProblemAsync(context, failure.Status, failure.Code, failure.Message);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!context.Response.HasStarted && ex is JsonException or Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            await ProblemAsync(context, 400, "invalid-request", "The request body is not what this route takes.");
        }
    }

    private static bool Authorized(HttpContext context, string token)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header), Encoding.UTF8.GetBytes("Bearer " + token));
    }

    internal static Task ProblemAsync(HttpContext context, int status, string code, string title)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new AgentProblem(title, status, code), AgentJson.Options), context.RequestAborted);
    }
}

/// <summary>Every error answer of the local API.</summary>
public sealed record AgentProblem(string Title, int Status, string Code);

public static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
