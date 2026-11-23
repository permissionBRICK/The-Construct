using System.Net;
using System.Net.Http.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Constructd.Windows.Network;

namespace Constructd.Tests.Api;

/// <summary>The solo engine's LAN switch: the route, and the one firewall rule it stands for on Windows.</summary>
public sealed class LanAccessTests
{
    private sealed record LanAccess(bool Enabled);

    [Fact]
    public async Task A_solo_engines_admin_switches_lan_access_and_reads_it_back()
    {
        using var app = new TestApp(new Dictionary<string, string?> { ["Constructd:Solo"] = "true" });
        var admin = await app.CreateUserClientAsync("alice", Role.Admin);
        var user = await app.CreateUserClientAsync("bob");

        Assert.False((await admin.GetFromJsonAsync<LanAccess>("/api/v1/host/lan-access"))!.Enabled);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PutAsJsonAsync("/api/v1/host/lan-access", new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/host/lan-access", new { })).StatusCode);

        var on = await admin.PutAsJsonAsync("/api/v1/host/lan-access", new { enabled = true });

        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True(app.Service<InMemoryLanAccess>().Enabled);
        Assert.True((await admin.GetFromJsonAsync<LanAccess>("/api/v1/host/lan-access"))!.Enabled);
        await admin.PutAsJsonAsync("/api/v1/host/lan-access", new { enabled = false });
        Assert.False(app.Service<InMemoryLanAccess>().Enabled);
    }

    [Fact]
    public async Task A_shared_host_has_no_lan_switch()
    {
        using var app = new TestApp();
        var admin = await app.CreateUserClientAsync("alice", Role.Admin);

        Assert.Equal(HttpStatusCode.Conflict, (await admin.GetAsync("/api/v1/host/lan-access")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/v1/host/lan-access", new { enabled = true })).StatusCode);
        Assert.False(app.Service<InMemoryLanAccess>().Enabled);
    }

    [Fact]
    public async Task On_windows_lan_access_is_one_private_profile_rule_for_the_api_port()
    {
        var runner = new RecordingProcessRunner();
        var lan = new FirewallLanAccess(runner, new ConstructdOptions { ListenUrl = "https://0.0.0.0:7462" });

        await lan.SetAsync(true, CancellationToken.None);
        await lan.SetAsync(false, CancellationToken.None);
        runner.Default = new ProcessResult(1, "No rules match the specified criteria.", "", TimedOut: false);
        Assert.False(await lan.GetAsync(CancellationToken.None));

        Assert.All(runner.Calls, call => Assert.Equal("netsh.exe", call.FileName));
        Assert.Equal(["advfirewall", "firewall", "delete", "rule", "name=ConstructEngineLan"], runner[0].Arguments);
        Assert.Equal(["advfirewall", "firewall", "add", "rule", "name=ConstructEngineLan", "dir=in", "action=allow", "protocol=TCP", "localport=7462", "profile=private"],
            runner[1].Arguments);
        Assert.Equal(["advfirewall", "firewall", "delete", "rule", "name=ConstructEngineLan"], runner[2].Arguments);
        Assert.Equal(4, runner.Calls.Count);
    }
}
