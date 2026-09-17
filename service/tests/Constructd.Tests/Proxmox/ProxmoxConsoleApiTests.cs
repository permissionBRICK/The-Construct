using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Proxmox;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Proxmox;

public sealed class ProxmoxConsoleApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Primary_gateway_gets_a_VNC_endpoint_with_scoped_auth_cleanup_and_no_logged_secrets(bool fail)
    {
        var fixture = new ProxmoxInteractiveConsoleTests.FixtureRunner { Fail = fail };
        var commands = new RecordingProcessRunner().RespondStdout("""[{"type":"qemu","node":"pve1","name":"primary","vmid":101}]""");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var app = new TestApp(configureServices: services =>
            services.AddSingleton<IInteractiveConsole>(sp => new ProxmoxInteractiveConsole(commands,
                new ConstructdOptions { ListenAddress = "127.0.0.1", PublicHost = "pve.test",
                    Proxmox = new() { Node = "pve1", ConsolePorts = new(port, port) } },
                sp.GetRequiredService<IConsoleSessionStore>(), sp.GetRequiredService<IClock>(), fixture)));
        using var owner = await app.CreateUserClientAsync("owner", Constructd.Core.Domain.Role.Admin);
        var job = await owner.CreateVmAsync("primary");
        using var gateway = app.CreateVmTokenClient(job.VmToken());
        const string root = "/api/v1/vms/primary/console/sessions";
        var session = await (await gateway.PostJsonAsync(root, new { })).ReadAsync<JsonElement>();
        Assert.False(session.TryGetProperty("interactiveUrl", out _));
        Assert.DoesNotContain("Proxmox login", session.ToString());
        var path = root + "/" + session.GetProperty("sessionId").GetString();
        Assert.Equal(HttpStatusCode.Gone, (await owner.PostJsonAsync(path + "/connection", new { })).StatusCode);
        var response = await gateway.PostJsonAsync(path + "/connection", new { });
        var body = await response.Content.ReadAsStringAsync();
        var password = fixture.Environment["LC_PVE_TICKET"];
        if (fail)
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.DoesNotContain(password, body);
            Assert.DoesNotContain("SECRET-DEPENDENCY", body);
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            var connection = JsonSerializer.Deserialize<ConsoleConnection>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal("vnc", connection.Protocol);
            Assert.Equal("pve.test", connection.Host);
            Assert.Equal(port, connection.Port);
            using var client = await ProxmoxInteractiveConsoleTests.ConnectAsync(connection);
            Assert.Equal(HttpStatusCode.OK, (await gateway.PostJsonAsync(path + "/renew", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await gateway.DeleteAsync(path)).StatusCode);
            Assert.True(fixture.Process!.Exited.IsCompleted);
            Assert.Equal(HttpStatusCode.Gone, (await gateway.PostJsonAsync(path + "/connection", new { })).StatusCode);
        }
        Assert.DoesNotContain(password, app.Logs.AllText());
        Assert.DoesNotContain("SECRET-DEPENDENCY", app.Logs.AllText());
        var audit = await (await owner.GetAsync("/api/v1/audit?limit=1000")).ReadAsync<List<AuditResponse>>();
        Assert.All(audit, entry => Assert.DoesNotContain(password, entry.Detail ?? ""));
    }
}
