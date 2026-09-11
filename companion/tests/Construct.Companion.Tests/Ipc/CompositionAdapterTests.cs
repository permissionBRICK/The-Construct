using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Desktop;
using Construct.Companion.Host.Ipc;
using Construct.Companion.Host.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Construct.Companion.Tests.Ipc.HttpTests;
namespace Construct.Companion.Tests.Ipc;

public sealed class CompositionAdapterTests
{
    [Fact]
    public async Task RemoteTlsPinIsCheckedBeforeAuthenticatedHttpAndRedirectsAreNotFollowed()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        var received = 0; var authenticated = false;
        app.MapPost("/api", (HttpContext context) => { received++; authenticated = context.Request.Headers.Authorization == "Bearer fixture"; return Results.Json(new { ok = true }); });
        app.MapGet("/redirect", () => Results.Redirect("https://example.invalid/never"));
        await app.StartAsync();
        try
        {
            var handlers = new List<HttpClientHandler>();
            var api = new HttpRemoteApi(() => { var handler = new HttpClientHandler(); handlers.Add(handler); return handler; });
            var url = new Uri(app.Urls.Single() + "/api");
            var pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            var body = JsonSerializer.SerializeToElement(new { value = "safe" });
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => api.SendAsync(new("POST", url, _ => false, body, RemoteAuthentication.Token, new("fixture"))));
            Assert.DoesNotContain("fixture", error.ToString()); Assert.Equal(0, received);
            var result = await api.SendAsync(new("POST", url, actual => actual == pin, body, RemoteAuthentication.Token, new("fixture")));
            Assert.Equal(200, result.StatusCode); Assert.Equal(1, received); Assert.True(authenticated);
            var redirect = await api.SendAsync(new("GET", new Uri(app.Urls.Single()+"/redirect"), actual => actual == pin));
            Assert.Equal(302, redirect.StatusCode);
            await api.SendAsync(new("POST", url, actual => actual == pin, Authentication:RemoteAuthentication.Negotiate));
            Assert.True(handlers.Last().UseDefaultCredentials);
        }
        finally { await app.StopAsync(); }
    }
    private sealed class NotFoundHandler : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); }
    private sealed class RedirectHandler(string target, int hops) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.AbsoluteUri);
            if (Requested.Count <= hops) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(target + Requested.Count) } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"commit\":\"ok\"}") });
        }
    }
    [Fact]
    public async Task PublicUpdateFollowsHttpsRedirects()
    {
        var handler = new RedirectHandler("https://objects.example.test/manifest", 2);
        using var source = new HttpUpdateSource(new HttpClient(handler));
        var result = await source.GetJsonAsync(new Uri("https://github.example.test/releases/latest/download/manifest.json"));
        Assert.Equal("ok", result!["commit"]!.GetValue<string>());
        Assert.Equal(3, handler.Requested.Count);
    }
    [Fact]
    public async Task PublicUpdateRefusesRedirectsOffHttpsAndRedirectLoops()
    {
        using var downgrade = new HttpUpdateSource(new HttpClient(new RedirectHandler("http://plain.example.test/", 1)));
        Assert.Null(await downgrade.GetJsonAsync(new Uri("https://github.example.test/manifest.json")));
        using var loop = new HttpUpdateSource(new HttpClient(new RedirectHandler("https://loop.example.test/", 50)));
        Assert.Null(await loop.GetJsonAsync(new Uri("https://github.example.test/manifest.json")));
    }
    [Fact]
    public async Task PublicUpdateNotFoundIsMappedWithoutThrowing()
    {
        using var source = new HttpUpdateSource(new HttpClient(new NotFoundHandler()));
        var result = await source.GetJsonAsync(new Uri("https://example.test/missing"));
        Assert.True(result!["notFound"]!.GetValue<bool>());
    }
    [Fact]
    public async Task DesktopAndIpcShareOneSettingsStore()
    {
        await using var h = await Harness.Start(); var store = h.App.Services.GetRequiredService<IpcSettings>();
        using var stream = await h.Client.GetAsync("/v1/events",HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync()); await reader.ReadLineAsync(); await reader.ReadLineAsync();
        store.SaveBounds("panel", new(1,2,300,400));
        var published = await Until(reader, d => d["type"]?.GetValue<string>() == "settings" && d["settings"]?["windows"]?["panel"] is not null);
        Assert.Equal(300, published["settings"]!["windows"]!["panel"]!["width"]!.GetValue<int>());
        var changes = 0; store.Changed += _ => changes++;
        using var response = await h.Client.PutAsJsonAsync("/v1/settings", new { repatchDelaySeconds = 1000, uiTheme = "native" });
        response.EnsureSuccessStatusCode(); Assert.Equal(600, store.Read().RepatchDelaySeconds);
        Assert.Equal("native", store.Read().UiTheme); Assert.Equal(new WindowBounds(1,2,300,400), store.Bounds("panel")); Assert.Equal(1, changes);
    }
    [Fact]
    public async Task RealDesktopLauncherAcceptsDispatcherLifecyclePlan()
    {
        var process = new FakeDesktopProcess();
        await using var h = await Harness.Start(s =>
        {
            var files = (IFileSystem)s.Last(d => d.ServiceType == typeof(IFileSystem)).ImplementationInstance!;
            s.AddSingleton<ILauncher>(new DesktopLauncher(process, files));
        });
        h.Get<FakePrompts, IPrompts>().Confirmations.Enqueue(true);
        using var response = await h.Post("/v1/instances/agent-vm/messages", new { type = "command", id = "reinstall" });
        response.EnsureSuccessStatusCode();
        var launch = Assert.Single(process.Invocations);
        Assert.Equal("cmd.exe", launch.FileName); Assert.False(launch.CreateNoWindow);
        Assert.Contains("-Verb RunAs", System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(launch.Arguments[^1])));
    }
    [Fact]
    public async Task DiagnosticHostDoesNotPublishOrDeleteExistingEndpointOrStartJobs()
    {
        await using var app = IpcServer.Build(s => s.AddCompanionFakes().AddCompanionHost(runtimeJobs:false), new(PublishEndpoint:false));
        var files = app.Services.GetRequiredService<IFileSystem>();
        var endpoint = Path.Combine(app.Services.GetRequiredService<IpcSettings>().Directory,"endpoint.json");
        files.WriteFileAtomic(endpoint, "existing"u8);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
        using var response = await client.GetAsync("/v1/health"); response.EnsureSuccessStatusCode();
        Assert.Empty(((FakeInstanceConnections)app.Services.GetRequiredService<IInstanceConnections>()).Transports);
        await app.StopAsync(); Assert.Equal("existing"u8.ToArray(),files.ReadFile(endpoint));
    }
    [Fact]
    public async Task DesktopWatcherSeesNestedSettingsReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(),"companion-watch-"+Guid.NewGuid().ToString("N"));
        var files = new HostFileSystem(); files.CreateDirectory(Path.Combine(root,"companion"));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var watch = files.Watch(root,()=>changed.TrySetResult());
            files.WriteFileAtomic(Path.Combine(root,"companion","settings.json"),"{}"u8);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { Directory.Delete(root,true); }
    }
}
