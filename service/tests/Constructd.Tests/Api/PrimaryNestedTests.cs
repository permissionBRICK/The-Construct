using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Jobs;
using Constructd.Core.Domain;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

public sealed class PrimaryNestedTests
{
    [Fact]
    public async Task RunningAndSavedKeepPendingUntilColdStart()
    {
        using var app = new TestApp();
        using var owner = await app.CreateUserClientAsync("owner");
        await owner.CreateVmAsync("parent");
        (await owner.PutAsJsonAsync("/api/v1/vms/parent/nested", new { enabled = true })).EnsureSuccessStatusCode();
        var read = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/parent/nested");
        Assert.True(read.GetProperty("pending").GetBoolean());
        Assert.False(read.GetProperty("current").GetBoolean());
        Assert.DoesNotContain(app.Driver.Calls, c => c.StartsWith("nested:"));
        app.Driver.SetState("parent", VmState.Saved);
        (await owner.PostAsJsonAsync("/api/v1/vms/parent/power", new { action = "start" })).EnsureSuccessStatusCode();
        Assert.DoesNotContain(app.Driver.Calls, c => c.StartsWith("nested:"));
        app.Driver.SetState("parent", VmState.Off);
        (await owner.PostAsJsonAsync("/api/v1/vms/parent/power", new { action = "start" })).EnsureSuccessStatusCode();
        Assert.True(app.Driver.NestedValues["parent"]);
        var calls = app.Driver.Calls.ToArray();
        Assert.True(Array.IndexOf(calls, "nested:parent:True") < Array.LastIndexOf(calls, "start:parent"));
        read = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/parent/nested");
        Assert.False(read.GetProperty("pending").GetBoolean());
    }

    [Fact]
    public async Task SelectionRequiresPolicyAndOwnershipButAdminCanOverride()
    {
        using var app = new TestApp();
        using var owner = await app.CreateUserClientAsync("owner");
        using var other = await app.CreateUserClientAsync("other");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await owner.CreateVmAsync("parent");
        await app.Users.UpdateAsync((await app.Users.GetAsync("owner", default))! with { AllowNested = false }, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync("/api/v1/vms/parent/nested", new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync("/api/v1/vms/parent/nested", new { enabled = false })).StatusCode);
        (await admin.PutAsJsonAsync("/api/v1/vms/parent/nested", new { enabled = true })).EnsureSuccessStatusCode();
        app.Driver.SetState("parent", VmState.Off);
        (await owner.PostAsJsonAsync("/api/v1/vms/parent/power", new { action = "start" })).EnsureSuccessStatusCode();
        Assert.True(app.Driver.NestedValues["parent"]);
        (await owner.PutAsJsonAsync("/api/v1/vms/parent/nested", new { enabled = false })).EnsureSuccessStatusCode();
        app.Driver.NestedAvailable = false;
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/v1/vms/parent/nested", new { enabled = true })).StatusCode);
    }

    [Fact]
    public async Task OffAppliesImmediatelyAndFailedApplyRetainsDesiredValue()
    {
        using var app = new TestApp();
        using var owner = await app.CreateUserClientAsync("owner");
        await owner.CreateVmAsync("parent");
        app.Driver.SetState("parent", VmState.Off);
        app.Driver.PowerFailure = new IOException("test");
        Assert.False((await owner.PutAsJsonAsync("/api/v1/vms/parent/nested", new { enabled = true })).IsSuccessStatusCode);
        Assert.True((await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/parent/nested")).GetProperty("pending").GetBoolean());
        app.Driver.PowerFailure = null;
        (await owner.PutAsJsonAsync("/api/v1/vms/parent/nested", new { enabled = true })).EnsureSuccessStatusCode();
        Assert.True(app.Driver.NestedValues["parent"]);
        var vm = (await app.Vms.GetAsync("parent", default))!;
        Assert.Null(await app.Service<PrimaryNestedSettings>().GetAsync(vm with { Created = vm.Created.AddSeconds(1) }, default));
    }
}
