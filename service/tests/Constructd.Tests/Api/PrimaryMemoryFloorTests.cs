using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

public sealed class PrimaryMemoryFloorTests
{
    private static TestApp Proxmox() => new(new Dictionary<string, string?> { ["Constructd:Backend"] = "proxmox" });
    private static Task<JsonElement> Floor(HttpClient client, string vm) => client.GetFromJsonAsync<JsonElement>($"/api/v1/vms/{vm}/memory-floor");
    // A host-default change is applied off the request.
    private static Task Pass(TestApp app) => app.Service<PrimaryMemoryFloor>().Background;

    [Fact]
    public async Task CreationAppliesTheSixGigabyteHostDefaultToTheRunningVm()
    {
        using var app = Proxmox();
        using var owner = await app.CreateUserClientAsync("owner");
        await owner.CreateVmAsync("vm");
        var calls = app.Driver.Calls.ToArray();
        // After the VM runs (create starts it), so first boot and provisioning keep all 8 GB.
        Assert.True(Array.IndexOf(calls, "create:vm") < Array.IndexOf(calls, "memory-floor:vm:6144"));
        var floor = await Floor(owner, "vm");
        Assert.True(floor.GetProperty("minimumRamGb").ValueKind == JsonValueKind.Null);
        Assert.Equal(6L << 30, floor.GetProperty("hostMinimumRamBytes").GetInt64());
        Assert.Equal(6144, floor.GetProperty("effectiveMinimumRamMb").GetInt32());
        Assert.Equal("host", floor.GetProperty("source").GetString());
        Assert.False(floor.GetProperty("mayEdit").GetBoolean());
    }

    [Fact]
    public async Task OnlyAdminsOverrideAndTheOverrideAppliesToTheRunningVmAtOnce()
    {
        using var app = Proxmox();
        using var owner = await app.CreateUserClientAsync("owner");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await owner.CreateVmAsync("vm");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync("/api/v1/vms/vm/memory-floor", new { minimumRamGb = 2 })).StatusCode);

        var put = await admin.PutAsJsonAsync("/api/v1/vms/vm/memory-floor", new { minimumRamGb = 7 });
        put.EnsureSuccessStatusCode();
        var view = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(7, view.GetProperty("minimumRamGb").GetInt32());
        Assert.Equal("vm", view.GetProperty("source").GetString());
        Assert.True(view.GetProperty("mayEdit").GetBoolean());
        Assert.Equal(7168, app.Driver.MemoryFloors["vm"]);

        // 0 never shrinks this VM; a floor at or above its RAM does the same.
        (await admin.PutAsJsonAsync("/api/v1/vms/vm/memory-floor", new { minimumRamGb = 0 })).EnsureSuccessStatusCode();
        Assert.Null(app.Driver.MemoryFloors["vm"]);
        Assert.True((await Floor(owner, "vm")).GetProperty("effectiveMinimumRamMb").ValueKind == JsonValueKind.Null);
        (await admin.PutAsJsonAsync("/api/v1/vms/vm/memory-floor", new { minimumRamGb = 8 })).EnsureSuccessStatusCode();
        Assert.Null(app.Driver.MemoryFloors["vm"]);

        // null follows the host default again.
        (await admin.PutAsJsonAsync("/api/v1/vms/vm/memory-floor", new { minimumRamGb = (int?)null })).EnsureSuccessStatusCode();
        Assert.Equal(6144, app.Driver.MemoryFloors["vm"]);

        foreach (var invalid in new[] { -1, 1025 })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/vms/vm/memory-floor", new { minimumRamGb = invalid })).StatusCode);
    }

    [Fact]
    public async Task HostDefaultChangesReachRunningVmsAndNullTurnsBallooningOff()
    {
        using var app = Proxmox();
        using var owner = await app.CreateUserClientAsync("owner");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await owner.CreateVmAsync("vm");
        await owner.CreateVmAsync("pinned");
        (await admin.PutAsJsonAsync("/api/v1/vms/pinned/memory-floor", new { minimumRamGb = 5 })).EnsureSuccessStatusCode();

        var config = await admin.GetFromJsonAsync<JsonElement>("/api/v1/host/config");
        Assert.Equal(6L << 30, config.GetProperty("virtualization").GetProperty("minimumRamBytes").GetInt64());
        (await admin.PutAsJsonAsync("/api/v1/host/config", new { virtualization = new { nestedDefault = true, nestedSelectable = true, minimumRamBytes = 4L << 30 } })).EnsureSuccessStatusCode();
        await Pass(app);
        Assert.Equal(4096, app.Driver.MemoryFloors["vm"]);
        Assert.Equal(5120, app.Driver.MemoryFloors["pinned"]);

        (await admin.PutAsJsonAsync("/api/v1/host/config", new { virtualization = new { nestedDefault = true, nestedSelectable = true, minimumRamBytes = (long?)null } })).EnsureSuccessStatusCode();
        await Pass(app);
        Assert.Null(app.Driver.MemoryFloors["vm"]);
        Assert.Equal(5120, app.Driver.MemoryFloors["pinned"]);

        // A client that does not know the field keeps the built-in default rather than turning it off.
        (await admin.PutAsJsonAsync("/api/v1/host/config", new { virtualization = new { nestedDefault = true, nestedSelectable = true } })).EnsureSuccessStatusCode();
        await Pass(app);
        Assert.Equal(6144, app.Driver.MemoryFloors["vm"]);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/host/config",
            new { virtualization = new { nestedDefault = true, nestedSelectable = true, minimumRamBytes = 512L << 20 } })).StatusCode);
    }

    [Fact]
    public async Task SavedVmsGetTheFloorWhenTheyResume()
    {
        using var app = Proxmox();
        using var owner = await app.CreateUserClientAsync("owner");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await owner.CreateVmAsync("vm");
        app.Driver.SetState("vm", VmState.Saved);
        (await admin.PutAsJsonAsync("/api/v1/vms/vm/memory-floor", new { minimumRamGb = 3 })).EnsureSuccessStatusCode();
        Assert.Equal(6144, app.Driver.MemoryFloors["vm"]);
        (await owner.PostAsJsonAsync("/api/v1/vms/vm/power", new { action = "start" })).EnsureSuccessStatusCode();
        Assert.Equal(3072, app.Driver.MemoryFloors["vm"]);
        var calls = app.Driver.Calls.ToArray();
        Assert.True(Array.LastIndexOf(calls, "start:vm") < Array.LastIndexOf(calls, "memory-floor:vm:3072"));
    }

    [Fact]
    public async Task ColdStartsBootWithFullRamAndGetTheFloorBackOnceRunning()
    {
        using var app = Proxmox();
        using var owner = await app.CreateUserClientAsync("owner");
        await owner.CreateVmAsync("vm");
        app.Driver.SetState("vm", VmState.Off);
        (await owner.PostAsJsonAsync("/api/v1/vms/vm/power", new { action = "start" })).EnsureSuccessStatusCode();
        var calls = app.Driver.Calls.ToArray();
        var start = Array.LastIndexOf(calls, "start:vm");
        Assert.Equal("memory-floor:vm:none", calls[..start].Last(c => c.StartsWith("memory-floor:", StringComparison.Ordinal)));
        Assert.Equal("memory-floor:vm:6144", calls[start..].First(c => c.StartsWith("memory-floor:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheHostPassSkipsAVmBusyWithAnOperation()
    {
        using var app = Proxmox();
        using var owner = await app.CreateUserClientAsync("owner");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await owner.CreateVmAsync("vm");
        await using (await app.Service<IVmOperationGate>().TryAcquireAsync("vm", "test-operation", default))
        {
            (await admin.PutAsJsonAsync("/api/v1/host/config", new { virtualization = new { nestedDefault = true, nestedSelectable = true, minimumRamBytes = 2L << 30 } })).EnsureSuccessStatusCode();
            await Pass(app);
            Assert.Equal(6144, app.Driver.MemoryFloors["vm"]);
        }
        await app.Service<PrimaryMemoryFloor>().ApplyAllAsync(default);
        Assert.Equal(2048, app.Driver.MemoryFloors["vm"]);
    }

    [Fact]
    public async Task AFailingApplyNeverFailsTheStartAndTheSettingIsKept()
    {
        using var app = Proxmox();
        using var owner = await app.CreateUserClientAsync("owner");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await owner.CreateVmAsync("vm");
        app.Driver.MemoryFloorFailure = new IOException("node busy");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/v1/vms/vm/memory-floor", new { minimumRamGb = 4 })).StatusCode);
        Assert.Equal(4, (await Floor(admin, "vm")).GetProperty("minimumRamGb").GetInt32());
        app.Driver.SetState("vm", VmState.Off);
        (await owner.PostAsJsonAsync("/api/v1/vms/vm/power", new { action = "start" })).EnsureSuccessStatusCode();
        Assert.Equal(VmState.Running, (await app.Vms.GetAsync("vm", default))!.State);
        app.Driver.MemoryFloorFailure = null;
        app.Driver.SetState("vm", VmState.Off);
        (await owner.PostAsJsonAsync("/api/v1/vms/vm/power", new { action = "start" })).EnsureSuccessStatusCode();
        Assert.Equal(4096, app.Driver.MemoryFloors["vm"]);
    }

    [Fact]
    public async Task OtherPlatformsNeitherOfferNorApplyIt()
    {
        using var app = new TestApp();
        using var owner = await app.CreateUserClientAsync("owner");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await owner.CreateVmAsync("vm");
        Assert.DoesNotContain(app.Driver.Calls, c => c.StartsWith("memory-floor:", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.GetAsync("/api/v1/vms/vm/memory-floor")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/v1/vms/vm/memory-floor", new { minimumRamGb = 4 })).StatusCode);
        var health = await owner.GetFromJsonAsync<JsonElement>("/api/v1/health");
        Assert.DoesNotContain(health.GetProperty("apiFeatures").EnumerateArray(), f => f.GetString() == "primary-memory-floor");
    }
}
