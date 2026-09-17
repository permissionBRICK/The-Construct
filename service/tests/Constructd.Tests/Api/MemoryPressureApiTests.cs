using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

public class MemoryPressureApiTests
{
    [Fact]
    public async Task Scheduler_save_is_visible_in_status_inventory_and_audit()
    {
        using var app = new TestApp(new Dictionary<string, string?>
        {
            ["Constructd:Idle:SchedulerEnabled"] = "true", ["Constructd:Idle:TickSeconds"] = "3600"
        });
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var now = app.Clock.UtcNow;
        await app.Vms.AddAsync(new("work-vm", "admin", 2, 4, 20, now, VmState.Running, null, null,
            new(120, IdleAction.Save), []), 10, default);
        app.Driver.SetState("work-vm", VmState.Running);
        await app.Vms.SaveActivityAsync(new("work-vm", false, [], now), default);
        var ledger = app.Service<InMemoryCapacityLedger>();
        void Sample() => ledger.Inventory = new(1, app.Clock.UtcNow, true, 100, 0, 0, 0, 5, 5, 8, null, 0, null,
            [], [], [], RamUsedBytes: 95, MeasuredVms:
            [new("work-vm", "id", VmState.Running, "running", 2, 2, 20, 20, false, null, [], null, "/", true)]);
        Sample();
        await app.Service<IIdlePolicyEngine>().EvaluateAsync(now, default);
        app.Clock.UtcNow = now.AddMinutes(20); Sample();
        await app.Service<IIdlePolicyEngine>().EvaluateAsync(app.Clock.UtcNow, default);
        var status = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/host/status")).GetProperty("memoryPressure");
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal("pressure", status.GetProperty("state").GetString());
        Assert.Equal("work-vm", status.GetProperty("lastAction").GetProperty("vmName").GetString());
        Assert.Equal(95, status.GetProperty("usedPercent").GetDecimal());
        var inventory = await admin.GetFromJsonAsync<JsonElement>("/api/v1/vms");
        Assert.Equal("memory-pressure", inventory.EnumerateArray().Single().GetProperty("savedBy").GetString());
        var audit = await admin.GetFromJsonAsync<JsonElement>("/api/v1/audit");
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "vm.pressure-save");
        await app.Service<IHostConfigStore>().SetAsync("memoryPressure", new MemoryPressureConfig(Enabled: false), "admin", default);
        var disabled = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/host/status")).GetProperty("memoryPressure");
        Assert.False(disabled.GetProperty("enabled").GetBoolean());
        Assert.Equal("off", disabled.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Global_scheduler_switch_reports_pressure_off()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var status = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/host/status")).GetProperty("memoryPressure");
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.Equal("off", status.GetProperty("state").GetString());
    }
}
