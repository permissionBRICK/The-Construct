using Constructd.Api.Https;
using Constructd.Core.Configuration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;

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
}
