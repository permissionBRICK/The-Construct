using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Core.State;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Ipc;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
namespace Construct.Companion.Tests.Ipc;

public sealed class HttpTests
{
    [Fact]
    public async Task CorruptSettingsUseDefaultsAndLogARefusalSafeDiagnostic()
    {
        await using var h = await Harness.Start(); var settings = h.App.Services.GetRequiredService<IpcSettings>();
        // The enrichment service writes activeInstance at startup; corrupt the file only after that write.
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8))) while (!h.Files.FileExists(settings.PathName)) await Task.Delay(10, deadline.Token);
        h.Files.WriteFileAtomic(settings.PathName, "{invalid"u8);
        using var response = await h.Client.GetAsync("/v1/settings"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(h.App.Services.GetRequiredService<IpcLogs>().Read(20), line => line == "Invalid Companion settings; using defaults.");
    }
    [Fact]
    public async Task NarrowShapesMatchPanelAndAggregateOfferKey()
    {
        await using var h = await Harness.Start();
        var state = h.App.Services.GetRequiredService<Host.Dispatch.StateAggregation>();
        state.Publish("agent-vm", new { type = "hostAdminOffer", instance = "agent-vm", offer = new { host = "host.example", url = "https://host.example:7462" } });
        foreach (var kind in new[] { "forwards", "children", "idlePolicy" }) state.Publish("agent-vm", new JsonObject { ["type"] = kind, ["instance"] = "agent-vm", [kind] = new JsonObject { ["visible"] = true } });
        var snapshot = await h.Client.GetFromJsonAsync<JsonObject>("/v1/instances/agent-vm/snapshot");
        foreach (var kind in new[] { "forwards", "children", "idlePolicy", "hostAdminOffer" })
        {
            Assert.Equal(kind, snapshot![kind]!["type"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(snapshot[kind]![kind == "hostAdminOffer" ? "offer" : kind], snapshot["state"]!["state"]![kind]));
        }
        Assert.Null(snapshot!["hostAdminOffer"]!["hostAdminOffer"]);
    }
    [Fact]
    public async Task AuthHostProblemsAndEndpointLifetime()
    {
        await using var host = await Harness.Start();
        using var health = await host.Client.GetAsync("/v1/health"); Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var doc = await health.Content.ReadFromJsonAsync<JsonObject>(); Assert.True(doc!["ok"]!.GetValue<bool>()); Assert.Equal(1, doc["ipcApiVersion"]!.GetValue<int>()); Assert.Null(doc["token"]);
        host.Client.DefaultRequestHeaders.Authorization = null;
        await host.Problem("GET", "/v1/state", 401, "unauthorized");
        host.Authenticate(); host.Client.DefaultRequestHeaders.Host = "evil.example:" + host.Port;
        await host.Problem("GET", "/v1/health", 421, "misdirectedRequest");
        host.Client.DefaultRequestHeaders.Host = "localhost:" + (host.Port + 1);
        await host.Problem("GET", "/v1/state", 421, "misdirectedRequest");
        host.Client.DefaultRequestHeaders.Host = "localhost:" + host.Port;
        await host.Problem("GET", "/v1/not-a-route", 404, "routeNotFound");
        await host.Problem("POST", "/v1/settings", 405, "routeNotFound");
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/instances/agent-vm/messages") { Content = new StringContent("[") };
        using var bad = await host.Client.SendAsync(request); Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode); Assert.Equal("application/problem+json", bad.Content.Headers.ContentType!.MediaType);
        Assert.False(health.Headers.Contains("Access-Control-Allow-Origin"));
        await host.App.StopAsync(); Assert.False(host.Files.FileExists(host.EndpointPath));
    }
    [Fact]
    public async Task StateSettingsSelectionActivationAndLogsRoutes()
    {
        await using var host = await Harness.Start();
        using var ready = await host.Post("/v1/instances/agent-vm/messages", new { type = "ready" }); Assert.Equal(HttpStatusCode.Accepted, ready.StatusCode);
        var snapshot = await host.Client.GetFromJsonAsync<JsonObject>("/v1/instances/agent-vm/snapshot");
        Assert.Equal("state", snapshot!["state"]!["type"]!.GetValue<string>()); Assert.Equal("agent-vm", snapshot["state"]!["state"]!["instance"]!.GetValue<string>());
        Assert.True(snapshot["state"]!["state"]!["online"]!.GetValue<bool>()); Assert.Null(snapshot["state"]!["state"]!["connectedInstance"]); Assert.Null(snapshot["state"]!["state"]!["instances"]);
        foreach (var key in new[] { "settings", "audio", "forwards", "children", "idlePolicy", "hostAdminOffer" }) Assert.NotNull(snapshot[key]);
        using var update = await host.Client.PutAsJsonAsync("/v1/settings", new { forwards = new { hostLabel = "desktop" }, repatchDelaySeconds = 1000 });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var settings = await update.Content.ReadFromJsonAsync<JsonObject>(); Assert.True(settings!["forwards"]!["enabled"]!.GetValue<bool>()); Assert.Equal(600, settings["repatchDelaySeconds"]!.GetValue<int>());
        using var select = await host.Post("/v1/instances/agent-vm/select", new { }); Assert.Equal(HttpStatusCode.Accepted, select.StatusCode);
        var full = await host.Client.GetFromJsonAsync<JsonObject>("/v1/state"); Assert.Equal("agent-vm", full!["activeInstance"]!.GetValue<string>());
        using var activation = await host.Post("/v1/ui/activate", new { view = "settings", instance = "agent-vm" }); Assert.Equal(HttpStatusCode.Accepted, activation.StatusCode);
        Assert.Equal("settings", host.Get<FakeCompanionDesktop, ICompanionDesktop>().Activations.Single().View);
        await host.Problem("GET", "/v1/instances/missing/snapshot", 404, "instanceNotFound");
        using var log = await host.Client.GetAsync("/v1/logs?lines=1"); Assert.Single((await log.Content.ReadFromJsonAsync<string[]>())!);
        await host.Problem("GET", "/v1/logs?lines=-1", 400, "invalidLines");
    }
    [Fact]
    public async Task SseFilteringAndReadySnapshotAndVisibleRefusal()
    {
        await using var host = await Harness.Start();
        using var stream = await host.Client.GetAsync("/v1/events?instance=agent-vm", HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync());
        Assert.Equal(": connected", await reader.ReadLineAsync()); Assert.Equal("", await reader.ReadLineAsync());
        var events = host.App.Services.GetRequiredService<IpcEvents>();
        events.Message("other", new { type = "wrong" }); events.HostAdmin("h", new { type = "wrong" }); events.Companion(new { type = "notification" });
        var first = await ReadEvent(reader); Assert.Equal("companion", first.Event); Assert.Equal("notification", first.Data["type"]!.GetValue<string>());
        using var response = await host.Post("/v1/instances/agent-vm/messages", new { type = "command", id = "unknown" }); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var refusal = await Until(reader, d => d["message"]?["type"]?.GetValue<string>() == "lifecyclePrepared"); Assert.NotNull(refusal["message"]!["error"]);
        using var ready = await host.Post("/v1/instances/agent-vm/messages", new { type = "ready" }); Assert.Equal(HttpStatusCode.Accepted, ready.StatusCode);
        var snapshot = await Until(reader, d => d["message"]?["type"]?.GetValue<string>() == "state"); Assert.NotNull(snapshot["message"]!["state"]);
    }
    [Fact]
    public async Task ForwardSpoolToSseToCloseCommand()
    {
        await using var host = await Harness.Start();
        using var stream = await host.Client.GetAsync("/v1/events?instance=agent-vm", HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync()); await reader.ReadLineAsync(); await reader.ReadLineAsync();
        var transport = host.Get<FakeInstanceConnections, IInstanceConnections>().Forwards["agent-vm"];
        transport.View["requests"] = new JsonArray(new JsonObject { ["v"] = 1, ["id"] = "web", ["vmPort"] = 5173 });
        await host.App.Services.GetRequiredService<CompanionInstances>().Get("agent-vm").Runtime!.Forwarder!.ReconcileAsync();
        var snapshot = await Until(reader, d => d["message"]?["type"]?.GetValue<string>() == "forwards" && d["message"]?["forwards"]?["items"] is JsonArray a && a.Count == 1);
        Assert.Equal("agent-vm", snapshot["instance"]!.GetValue<string>());
        using var response = await host.Post("/v1/instances/agent-vm/messages", new { type = "command", id = "closeForward", forward = "web" }); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(transport.Ssh.Tunnels.Single().Process.Stopped);
        Assert.Empty(transport.View["requests"]!.AsArray());
    }
    [Fact]
    public async Task QuitRespondsBeforeStoppingAndRemovesEndpoint()
    {
        await using var host = await Harness.Start();
        using var response = await host.Post("/v1/quit", new { reason = "update" }); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonObject>())!["accepted"]!.GetValue<bool>());
        await host.App.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(host.Files.FileExists(host.EndpointPath));
    }
    internal static async Task<JsonObject> Until(StreamReader reader, Func<JsonObject, bool> predicate)
    { for (var i = 0; i < 100; i++) { var item = await ReadEvent(reader); if (predicate(item.Data)) return item.Data; } throw new InvalidOperationException("Expected event was not delivered."); }
    internal static async Task<(string Event, JsonObject Data)> ReadEvent(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6)); string kind = "", data = "";
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        { if (line.Length == 0 && data.Length > 0) return (kind, JsonNode.Parse(data)!.AsObject()); if (line.StartsWith("event: ")) kind = line[7..]; if (line.StartsWith("data: ")) data = line[6..]; }
        throw new InvalidOperationException("Event stream ended.");
    }
    internal sealed class Harness(WebApplication app, HttpClient client, Endpoint endpoint) : IAsyncDisposable
    {
        public WebApplication App => app;
        public HttpClient Client => client;
        public int Port => endpoint.Port;
        public IFileSystem Files => app.Services.GetRequiredService<IFileSystem>();
        public string EndpointPath => Path.Combine(app.Services.GetRequiredService<IpcSettings>().Directory, "endpoint.json");
        public T Get<T, TInterface>() where T : class where TInterface : notnull => (app.Services.GetRequiredService<TInterface>() as T)!;
        public void Authenticate() => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        public static async Task<Harness> Start(Action<IServiceCollection>? configure = null)
        {
            var app = IpcServer.Build(s => { s.AddCompanionFakes(); configure?.Invoke(s); s.AddCompanionHost(); });
            try
            {
                await app.StartAsync();
                var path = Path.Combine(app.Services.GetRequiredService<IpcSettings>().Directory, "endpoint.json");
                var endpoint = JsonSerializer.Deserialize<Endpoint>(app.Services.GetRequiredService<IFileSystem>().ReadFile(path)!, IpcJson.Options)!;
                var result = new Harness(app, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{endpoint.Port}"), Timeout = TimeSpan.FromSeconds(10) }, endpoint); result.Authenticate(); return result;
            }
            catch { await app.DisposeAsync(); throw; }
        }
        public async Task<HttpResponseMessage> Post(string route, object body) { var response = await client.PostAsJsonAsync(route, body); await app.Services.GetRequiredService<DispatchQueue>().DrainAsync().WaitAsync(TimeSpan.FromSeconds(10)); return response; }
        public async Task Problem(string method, string route, int status, string code)
        { using var response = await client.SendAsync(new(new HttpMethod(method), route)); Assert.Equal(status, (int)response.StatusCode); var problem = await response.Content.ReadFromJsonAsync<JsonObject>(); Assert.Equal(code, problem!["code"]!.GetValue<string>()); Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType); }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
    }
}
