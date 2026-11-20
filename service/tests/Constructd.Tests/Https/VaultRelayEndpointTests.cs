using System.Net;
using Constructd.Api.Https;
using Constructd.Core.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Constructd.Tests.Https;

public sealed class VaultRelayEndpointTests
{
    private sealed class BoundServer : IServer
    {
        public BoundServer(params string[] addresses)
        {
            var feature = new ServerAddressesFeature();
            foreach (var address in addresses) feature.Addresses.Add(address);
            Features.Set<IServerAddressesFeature>(feature);
        }
        public IFeatureCollection Features { get; } = new FeatureCollection();
        public void Bind(string address) => Features.Get<IServerAddressesFeature>()!.Addresses.Add(address);
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }

    [Theory]
    // A plain-http loopback API (fake mode, the e2e tests, local development) is bound first.
    [InlineData("http://127.0.0.1:5000", new[] { "http://127.0.0.1:5000", "http://127.0.0.1:41234" }, 41234)]
    [InlineData("http://127.0.0.1:5000", new[] { "http://127.0.0.1:41234", "http://127.0.0.1:5000" }, 41234)]
    // Production: the API listens on https, the relay is the only plain-http loopback address.
    [InlineData("https://0.0.0.0:7462", new[] { "https://0.0.0.0:7462", "http://127.0.0.1:41234" }, 41234)]
    // A dynamic plain-http API port as well: Kestrel lists ListenUrl first and the relay last.
    [InlineData("http://127.0.0.1:0", new[] { "http://127.0.0.1:41000", "http://127.0.0.1:41234" }, 41234)]
    // Nothing but the API itself: there is no relay port yet.
    [InlineData("http://127.0.0.1:5000", new[] { "http://127.0.0.1:5000" }, 0)]
    public void The_relay_port_is_the_extra_listener_never_the_apis_own(string listenUrl, string[] bound, int expected)
    {
        var options = new ConstructdOptions { ListenUrl = listenUrl };
        Assert.Equal(expected, new VaultRelayEndpoint(new BoundServer(bound), options).Port);
    }

    [Fact]
    public void Nothing_is_cached_while_only_the_dynamic_api_port_is_bound()
    {
        // Kestrel publishes the API's address first; a request in between must not fix the relay on it.
        var server = new BoundServer("http://127.0.0.1:41000");
        var relay = new VaultRelayEndpoint(server, new ConstructdOptions { ListenUrl = "http://127.0.0.1:0" });
        Assert.Equal(0, relay.Port);
        server.Bind("http://127.0.0.1:41234");
        Assert.Equal(41234, relay.Port);
    }

    [Fact]
    public async Task On_kestrel_with_both_ports_dynamic_the_api_answers_and_the_relay_only_serves_the_vault()
    {
        var options = new ConstructdOptions { ListenUrl = "http://127.0.0.1:0" };
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        // As Program.cs composes them: ListenUrl first, the relay last.
        builder.WebHost.UseKestrel().UseUrls($"{options.ListenUrl};{VaultRelayEndpoint.ListenUrl(options)}");
        builder.Services.AddSingleton(options).AddSingleton<VaultRelayEndpoint>();
        await using var app = builder.Build();
        var relay = app.Services.GetRequiredService<VaultRelayEndpoint>();
        // The same filter as Program.cs.
        app.Use(async (http, next) =>
        {
            if (relay.IsRelayed(http) && !VaultRelayEndpoint.Allowed(http.Request.Path)) { http.Response.StatusCode = 404; return; }
            await next();
        });
        app.MapGet("/api/v1/health", () => "ok");
        app.MapGet("/vault/", () => "vault");
        Assert.Equal(0, relay.Port);
        await app.StartAsync();
        try
        {
            var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ToArray();
            Assert.Equal(2, bound.Length);
            Assert.Equal(new Uri(bound[1]).Port, relay.Port);
            using var client = new HttpClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(bound[0] + "/api/v1/health")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(bound[0] + "/vault/")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(bound[1] + "/api/v1/health")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(bound[1] + "/vault/")).StatusCode);
        }
        finally { await app.StopAsync(); }
    }
}
