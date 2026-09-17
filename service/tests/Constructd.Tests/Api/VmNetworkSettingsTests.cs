using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

public sealed class VmNetworkSettingsTests
{
    private static TestApp Proxmox() => new(new Dictionary<string, string?> { ["Constructd:Backend"] = "proxmox" });

    [Fact]
    public async Task DirectCreationAndExposeNeverAllocatePortsAndGuestCanReadItsEndpoint()
    {
        using var app = Proxmox();
        using var owner = await app.CreateUserClientAsync("alice", allowHostForwards: false);
        await app.Service<IHostConfigStore>().SetAsync("network", new NetworkConfig(false, false, "direct"), "admin", default);
        app.Forwards.Failure = new InvalidOperationException("Direct creation must not allocate a relay.");
        var job = await owner.CreateVmAsync("vm");
        Assert.Equal(22, job.ResultElement("endpoint").GetProperty("sshPort").GetInt32());
        Assert.Null((await app.Vms.GetAsync("vm", default))!.SshForwardPort);
        using var guest = app.CreateVmTokenClient(job.VmToken());
        var endpoint = await guest.GetFromJsonAsync<JsonElement>("/api/v1/vms/vm/endpoint");
        Assert.Equal("vm.fake.local", endpoint.GetProperty("sshHost").GetString());
        Assert.Equal(22, endpoint.GetProperty("sshPort").GetInt32());
        var exposed = await guest.PostAsJsonAsync("/api/v1/vms/vm/forwards", new { vmPort = 3000, target = "host" });
        Assert.Equal(HttpStatusCode.OK, exposed.StatusCode);
        var body = await exposed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("direct", body.GetProperty("kind").GetString());
        Assert.Equal("http://vm.fake.local:3000/", body.GetProperty("url").GetString());
        Assert.Empty(await app.Forwards.ListAsync("vm", default));
        Assert.Empty(app.Forwards.Materialized);
        app.Forwards.Failure = null;
        Assert.Equal(HttpStatusCode.Created, (await guest.PostAsJsonAsync("/api/v1/vms/vm/forwards", new { vmPort = 3000, target = "client" })).StatusCode);
        app.Driver.ReportEndpoint = false;
        var unknown = await guest.GetAsync("/api/v1/vms/vm/endpoint");
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);
        Assert.Equal("no-address", (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await guest.PostAsJsonAsync("/api/v1/vms/vm/forwards", new { vmPort = 3000, target = "host" })).StatusCode);
    }

    [Fact]
    public async Task ModeChangesReleaseOldForwardsAndHostDefaultChangesWaitForStart()
    {
        using var app = Proxmox();
        using var owner = await app.CreateUserClientAsync("alice");
        await owner.CreateVmAsync("vm");
        await owner.PostAsJsonAsync("/api/v1/vms/vm/forwards", new { vmPort = 3000, target = "host" });
        var settings = app.Service<VmNetworkSettings>();
        var vm = (await app.Vms.GetAsync("vm", default))!;
        await app.Service<IHostConfigStore>().SetAsync("network", new NetworkConfig(true, true, "direct"), "admin", default);
        Assert.Equal("relayed", (await settings.CurrentAsync(vm, default)).Mode);
        app.Driver.SetState("vm", VmState.Off);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/v1/vms/vm/power", new { action = "start" })).StatusCode);
        vm = (await app.Vms.GetAsync("vm", default))!;
        Assert.Null(vm.SshForwardPort);
        Assert.Empty(await app.Forwards.ListAsync("vm", default));
        await settings.SaveAsync(vm, new(vm.Created, "direct", "10.0.3.50/22", "10.0.0.1", null), "admin", default);
        app.Driver.SetState("vm", VmState.Off);
        await settings.ApplyAsync(vm, VmState.Off, default);
        app.Driver.ReportEndpoint = false;
        Assert.Equal("10.0.3.50", (await settings.EndpointAsync(vm, default))!.SshHost);
        await settings.SaveAsync(vm, new(vm.Created, "relayed", null, null, null), "admin", default);
        vm = await settings.ApplyAsync(vm, VmState.Off, default);
        Assert.NotNull(vm.SshForwardPort);
        Assert.Equal(vm.SshForwardPort, (await settings.EndpointAsync(vm, default))!.SshPort);
    }

    [Theory]
    [InlineData(VmState.Running)]
    [InlineData(VmState.Saved)]
    [InlineData(VmState.Paused)]
    [InlineData(VmState.Off)]
    public async Task NetworkAppliesOnlyWhileOff(VmState state)
    {
        using var app = Proxmox();
        using var owner = await app.CreateUserClientAsync("alice");
        await owner.CreateVmAsync("vm");
        var vm = (await app.Vms.GetAsync("vm", default))!;
        var settings = app.Service<VmNetworkSettings>();
        await settings.SaveAsync(vm, new(vm.Created, "direct", "10.0.3.50/22", "10.0.0.1", null), "admin", default);
        app.Driver.SetState("vm", state);
        await settings.ApplyAsync(vm, state, default);
        Assert.Equal(state == VmState.Off ? "direct" : "relayed", (await settings.CurrentAsync(vm, default)).Mode);
        Assert.Equal(state == VmState.Off, app.Driver.Calls.Any(c => c.StartsWith("network:vm:")));
    }

    [Fact]
    public async Task DriverFailurePreventsStartAndKeepsDesiredSettingsForRetry()
    {
        using var app = Proxmox();
        using var owner = await app.CreateUserClientAsync("alice");
        await owner.CreateVmAsync("vm");
        var vm = (await app.Vms.GetAsync("vm", default))!;
        var settings = app.Service<VmNetworkSettings>();
        await settings.SaveAsync(vm, new(vm.Created, "direct", null, null, null), "admin", default);
        app.Driver.SetState("vm", VmState.Off);
        app.Driver.NetworkFailure = new Exception("fixture");
        var result = await owner.PostAsJsonAsync("/api/v1/vms/vm/power", new { action = "start" });
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        Assert.Equal("network-apply-failed", (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.DoesNotContain("start:vm", app.Driver.Calls);
        Assert.Equal("direct", (await settings.GetAsync(vm, default))!.Mode);
        app.Driver.NetworkFailure = null;
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/v1/vms/vm/power", new { action = "start" })).StatusCode);
        Assert.Equal("direct", (await settings.CurrentAsync(vm, default)).Mode);
        Assert.True(app.Driver.Calls.ToList().IndexOf("start:vm") > app.Driver.Calls.ToList().FindIndex(c => c.StartsWith("network:vm:")));
    }

    [Fact]
    public async Task OwnerModeRequiresPolicyAndAddressFieldsAlwaysRequireAdmin()
    {
        using var app = Proxmox();
        using var owner = await app.CreateUserClientAsync("alice");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        using var other = await app.CreateUserClientAsync("bob");
        var job = await owner.CreateVmAsync("vm");
        using var guest = app.CreateVmTokenClient(job.VmToken());
        const string route = "/api/v1/vms/vm/network";
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(route)).StatusCode);
        var denied = await owner.PutAsJsonAsync(route, new { mode = "direct" });
        Assert.Equal("policy-denied", (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        await app.Service<IHostConfigStore>().SetAsync("network", new NetworkConfig(true, true, "relayed", true), "admin", default);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(route, new { mode = "direct" })).StatusCode);
        foreach (var field in new[] { "address", "gateway", "dns" })
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync(route, new Dictionary<string, object?> { [field] = null })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(route,
            new { address = "10.0.3.50/22", gateway = "10.0.0.1", dns = new[] { "10.0.0.2" } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(route, new { mode = "relayed" })).StatusCode);
        var settings = await owner.GetFromJsonAsync<VmNetworkSettings.View>(route);
        Assert.Equal("10.0.3.50/22", settings!.DesiredAddress);
        Assert.False(settings.MaySetAddress);
        Assert.True(settings.MaySwitchMode);
    }

    [Theory]
    [InlineData("10.0.3.50", "10.0.0.1")]
    [InlineData("10.0.3.50/33", "10.0.0.1")]
    [InlineData("127.0.0.1/24", "10.0.0.1")]
    [InlineData("10.0.3.50/22", "--bad")]
    [InlineData("10.0.3.50/22", null)]
    public async Task InvalidFixedAddressIsRejected(string address, string? gateway)
    {
        using var app = Proxmox();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await admin.CreateVmAsync("vm");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/vms/vm/network",
            new { mode = "direct", address, gateway })).StatusCode);
        Assert.Null(await app.Service<VmNetworkSettings>().GetAsync((await app.Vms.GetAsync("vm", default))!, default));
    }

    [Fact]
    public async Task DesiredChangesStayPendingAndDoNotSurviveNameReuse()
    {
        using var app = Proxmox();
        using var owner = await app.CreateUserClientAsync("alice");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await owner.CreateVmAsync("vm");
        var vm = (await app.Vms.GetAsync("vm", default))!;
        var settings = app.Service<VmNetworkSettings>();
        await settings.SaveAsync(vm, new(vm.Created, "direct", "10.0.3.50/22", "10.0.0.1", null), "admin", default);
        var view = await owner.GetFromJsonAsync<VmNetworkSettings.View>("/api/v1/vms/vm/network");
        Assert.Equal("relayed", view!.EffectiveMode);
        Assert.Equal("direct", view.PendingMode);
        Assert.True(view.Pending);
        var inventory = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/vm");
        Assert.Equal("10.0.3.50/22", inventory.GetProperty("network").GetProperty("pendingAddress").GetString());
        Assert.Null(await settings.GetAsync(vm with { Created = vm.Created.AddSeconds(1) }, default));
        Assert.Equal("relayed", (await settings.CurrentAsync(vm with { Created = vm.Created.AddSeconds(1) }, default)).Mode);
    }

    [Fact]
    public async Task HyperVDoesNotExposeNetworkSettings()
    {
        using var app = new TestApp();
        using var owner = await app.CreateUserClientAsync("alice");
        await owner.CreateVmAsync("vm");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/v1/vms/vm/network")).StatusCode);
        var inventory = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/vm");
        Assert.False(inventory.TryGetProperty("network", out _));
    }

    [Fact]
    public async Task AppliedAndPendingNetworkSettingsSurviveServiceRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "construct-network-" + Guid.NewGuid().ToString("n"));
        var database = Path.Combine(directory, "service.db");
        var options = new Dictionary<string, string?> { ["Constructd:Backend"] = "proxmox" };
        try
        {
            using (var app = TestApp.WithSqlite(database, options))
            {
                using var owner = await app.CreateUserClientAsync("alice");
                await owner.CreateVmAsync("vm");
                var vm = (await app.Vms.GetAsync("vm", default))!;
                var settings = app.Service<VmNetworkSettings>();
                await settings.SaveAsync(vm, new(vm.Created, "direct", "10.0.3.50/22", "10.0.0.1", ["10.0.0.2"]), "admin", default);
                app.Driver.SetState("vm", VmState.Off);
                vm = await settings.ApplyAsync(vm, VmState.Off, default);
                await settings.SaveAsync(vm, new(vm.Created, "direct", "10.0.3.51/22", "10.0.0.1", null), "admin", default);
            }
            using (var app = TestApp.WithSqlite(database, options))
            {
                var vm = (await app.Vms.GetAsync("vm", default))!;
                var settings = app.Service<VmNetworkSettings>();
                var view = await settings.ProjectAsync(vm, true, default);
                Assert.Equal("direct", view.EffectiveMode);
                Assert.Equal("10.0.3.50", view.Address);
                Assert.Equal("10.0.3.51/22", view.PendingAddress);
                Assert.Null(vm.SshForwardPort);
                Assert.Null(await settings.GetAsync(vm with { Created = vm.Created.AddSeconds(1) }, default, applied: true));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
