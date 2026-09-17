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
            new { address = "203.0.113.50/24", gateway = "203.0.113.1", dns = new[] { "203.0.113.2" } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(route, new { mode = "relayed" })).StatusCode);
        var settings = await owner.GetFromJsonAsync<VmNetworkSettings.View>(route);
        Assert.Equal("203.0.113.50/24", settings!.DesiredAddress);
        Assert.False(settings.MaySetAddress);
        Assert.True(settings.MaySwitchMode);
    }

    [Theory]
    [InlineData("203.0.113.50", "203.0.113.1")]
    [InlineData("203.0.113.50/33", "203.0.113.1")]
    [InlineData("127.0.0.1/24", "203.0.113.1")]
    [InlineData("203.0.113.50/24", "--bad")]
    [InlineData("203.0.113.50/24", null)]
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
        await settings.SaveAsync(vm, new(vm.Created, "direct", "203.0.113.50/24", "203.0.113.1", null), "admin", default);
        var view = await owner.GetFromJsonAsync<VmNetworkSettings.View>("/api/v1/vms/vm/network");
        Assert.Equal("relayed", view!.EffectiveMode);
        Assert.Equal("direct", view.PendingMode);
        Assert.True(view.Pending);
        var inventory = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/vm");
        Assert.Equal("203.0.113.50/24", inventory.GetProperty("network").GetProperty("pendingAddress").GetString());
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
