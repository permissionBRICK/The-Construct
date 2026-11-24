using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using Constructd.Api.Auth;
using Constructd.Api.Https;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Constructd.Tests.Hosting;

public sealed class DualStackListenTests
{
    [Theory]
    [InlineData("https://0.0.0.0:7462", "https://+:7462")]
    [InlineData("http://0.0.0.0:0", "http://+:0")]
    [InlineData("https://0.0.0.0:7462/", "https://+:7462")]
    [InlineData("https://0.0.0.0:7462;http://127.0.0.1:7463", "https://+:7462;http://127.0.0.1:7463")]
    [InlineData("https://127.0.0.1:7462", "https://127.0.0.1:7462")]
    [InlineData("https://192.168.1.10:7462", "https://192.168.1.10:7462")]
    [InlineData("https://[::1]:7462", "https://[::1]:7462")]
    [InlineData("https://[::]:7462", "https://[::]:7462")]
    [InlineData("https://+:7462", "https://+:7462")]
    [InlineData("https://*:7462", "https://*:7462")]
    public void ListenUrls_normalizes_only_wildcard_ipv4_to_dual_stack(string input, string expected)
    {
        Assert.Equal(expected, ListenUrls.Normalize(input));
    }

    [Fact]
    public async Task DualStackListener_on_wildcard_accepts_both_ipv4_and_ipv6_and_blocks_double_bind()
    {
        var listener = DualStackListener.Start(IPAddress.Any, 0);
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.False(DualStackListener.CanBind(IPAddress.Any, port));

            var acceptV4 = listener.AcceptTcpClientAsync();
            using (var v4 = new TcpClient(AddressFamily.InterNetwork))
            {
                await v4.ConnectAsync(IPAddress.Loopback, port);
                using var serverV4 = await acceptV4.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(serverV4.Connected);
            }

            if (Socket.OSSupportsIPv6)
            {
                var acceptV6 = listener.AcceptTcpClientAsync();
                using (var v6 = new TcpClient(AddressFamily.InterNetworkV6))
                {
                    await v6.ConnectAsync(IPAddress.IPv6Loopback, port);
                    using var serverV6 = await acceptV6.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.True(serverV6.Connected);
                }
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void DualStackListener_ParseListenAddress_accepts_ipv4_ipv6_and_star()
    {
        Assert.Equal(IPAddress.Any, DualStackListener.ParseListenAddress("0.0.0.0"));
        Assert.Equal(IPAddress.Any, DualStackListener.ParseListenAddress("*"));
        Assert.Equal(IPAddress.IPv6Any, DualStackListener.ParseListenAddress("::"));
        Assert.Equal(IPAddress.Loopback, DualStackListener.ParseListenAddress("127.0.0.1"));
        Assert.Equal(IPAddress.IPv6Loopback, DualStackListener.ParseListenAddress("::1"));
        Assert.Throws<InvalidOperationException>(() => DualStackListener.ParseListenAddress("not-an-ip"));
    }

    [Fact]
    public async Task Kestrel_with_normalized_wildcard_listen_url_serves_both_ipv4_and_ipv6_and_vault_relay()
    {
        var options = new ConstructdOptions { ListenUrl = "http://0.0.0.0:0" };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls($"{ListenUrls.Normalize(options.ListenUrl)};{VaultRelayEndpoint.ListenUrl(options)}");
        await using var app = builder.Build();
        app.MapGet("/ping", () => Results.Text("pong"));
        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ToList();
        var relay = new VaultRelayEndpoint(app.Services.GetRequiredService<IServer>(), options);
        var relayPort = relay.Port;
        Assert.True(relayPort > 0);

        var apiUrl = addresses.First(a => !a.EndsWith($":{relayPort}", StringComparison.Ordinal));
        var apiPort = new Uri(apiUrl).Port;

        using var http = new HttpClient();
        Assert.Equal("pong", await http.GetStringAsync($"http://127.0.0.1:{apiPort}/ping"));
        if (Socket.OSSupportsIPv6)
        {
            Assert.Equal("pong", await http.GetStringAsync($"http://[::1]:{apiPort}/ping"));
        }
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("192.0.2.5", false)]
    [InlineData("::ffff:192.0.2.5", false)]
    [InlineData("2001:db8::1", false)]
    public async Task UpdateHandoffAuthenticationHandler_accepts_ipv4_mapped_loopback_from_dual_stack_socket(string remoteIp, bool expectedAuthenticated)
    {
        const string commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var token = new string('b', 64);
        var launcher = new FakeUpdaterLauncher();
        await launcher.LaunchAsync(new UpdateHandoff(
            "u1", commit, "/tmp/s", "/tmp/i", "/tmp/sc", "/tmp/d", "constructd", "prev",
            "https://127.0.0.1:7462/api/v1/health", "pin", "/tmp/exe", token, DateTimeOffset.UtcNow, 1, 30), default);
        var gate = new Constructd.Core.Services.InMemoryMaintenanceGate();
        gate.Enter(MaintenanceState.Maintenance, "u1");
        var release = new FakeReleaseInfo { Installed = new(commit, "1.0.0", null, "test") };
        var handler = new UpdateHandoffAuthenticationHandler(
            new StaticOptionsMonitor(new AuthenticationSchemeOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            launcher,
            release,
            gate);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/v1/health";
        context.Request.Headers.Authorization = new AuthenticationHeaderValue(UpdateHandoffAuthenticationHandler.SchemeName, token).ToString();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);

        await handler.InitializeAsync(new AuthenticationScheme(UpdateHandoffAuthenticationHandler.SchemeName, null, typeof(UpdateHandoffAuthenticationHandler)), context);
        var result = await handler.AuthenticateAsync();

        Assert.Equal(expectedAuthenticated, result.Succeeded);
    }

    private sealed class StaticOptionsMonitor(AuthenticationSchemeOptions value) : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue => value;
        public AuthenticationSchemeOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }
}
