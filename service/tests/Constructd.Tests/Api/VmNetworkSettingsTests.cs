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
}
