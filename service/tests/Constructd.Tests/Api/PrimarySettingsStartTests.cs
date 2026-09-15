using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Api.Jobs;
using Constructd.Fakes;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

public sealed class PrimarySettingsStartTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public async Task SqliteAppliesOffVmSettingsImmediatelyAndPendingSettingsBeforeStart(bool adopted, bool previouslyApplied, bool offAtSave)
    {
        var directory = Path.Combine(Path.GetTempPath(), "construct-settings-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var app = new TestApp(new Dictionary<string, string?> {
                ["Constructd:Persistence"] = "Sqlite", ["Constructd:DatabasePath"] = Path.Combine(directory, "test.db") });
            using var owner = await app.CreateUserClientAsync("alice");
            var vm = new Vm("parent", "alice", 4, previouslyApplied ? 4 : 8, 50, DateTimeOffset.UtcNow, VmState.Off,
                2222, null, IdlePolicy.Disabled, [], RamMb: adopted ? 8192 : null);
            Assert.Equal(VmAddOutcome.Added, await app.Vms.AddAsync(vm, 5, default));
            app.Driver.SetState(vm.Name, offAtSave ? VmState.Off : VmState.Running);
            (await owner.PutAsJsonAsync("/api/v1/vms/parent/cpu", new { cpus = 6 })).EnsureSuccessStatusCode();
            (await owner.PutAsJsonAsync("/api/v1/vms/parent/memory", new { ramGb = 4 })).EnsureSuccessStatusCode();
            if (offAtSave)
            {
                Assert.Equal(6, app.Driver.CpuCounts[vm.Name]);
                Assert.Equal(4, app.Driver.MemorySizes[vm.Name]);
                Assert.DoesNotContain("start:parent", app.Driver.Calls);
                var cpu = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/parent/cpu");
                var memory = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/parent/memory");
                Assert.False(cpu.GetProperty("pending").GetBoolean());
                Assert.False(memory.GetProperty("pending").GetBoolean());
            }
            else
            {
                Assert.Empty(app.Driver.CpuCounts);
                Assert.Empty(app.Driver.MemorySizes);
            }
            app.Driver.SetState(vm.Name, VmState.Off);
            (await owner.PostAsJsonAsync("/api/v1/vms/parent/lifecycle", new { action = "start" })).EnsureSuccessStatusCode();
            Assert.Equal(6, app.Driver.CpuCounts[vm.Name]);
            Assert.Equal(4, app.Driver.MemorySizes[vm.Name]);
            var calls = app.Driver.Calls.ToArray();
            Assert.True(Array.IndexOf(calls, "cpu:parent:6") < Array.IndexOf(calls, "start:parent"));
            Assert.True(Array.IndexOf(calls, "memory:parent:4") < Array.IndexOf(calls, "start:parent"));
            var current = (await app.Vms.GetAsync(vm.Name, default))!;
            Assert.Equal(6, current.Cpu);
            Assert.Equal(4, current.RamGb);
            Assert.Equal(4L << 30, current.RamBytes);
            var row = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/parent");
            Assert.Equal(6, row.GetProperty("cpu").GetInt32());
            Assert.Equal(4, row.GetProperty("ramGb").GetInt32());
            var reservations = (await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations;
            Assert.Equal(4L << 30, reservations.Where(r => r.VmName == vm.Name && r.Resource == ReservationResource.Ram).Sum(r => r.Amount));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("memory")]
    [InlineData("lifecycle")]
    public async Task PrimarySettingsAndStartWaitForAnIdleObservation(string resource)
    {
        using var app = new TestApp();
        var ledger = app.Service<InMemoryCapacityLedger>();
        ledger.Inventory = ledger.Inventory with { Complete = true, CpuLogical = 8, RamTotalBytes = 16L << 30, RamAvailableBytes = 12L << 30 };
        using var owner = await app.CreateUserClientAsync("alice");
        var vm = new Vm("parent", "alice", 4, 8, 50, app.Clock.UtcNow, VmState.Off, 2222, null, IdlePolicy.Disabled, []);
        await app.Vms.AddAsync(vm, 5, default);
        app.Driver.SetState(vm.Name, VmState.Off);
        Task<HttpResponseMessage> request;
        await using (await app.Service<IVmOperationGate>().AcquireAsync(vm.Name, "idle-evaluate", default))
        {
            request = resource == "lifecycle"
                ? owner.PostAsJsonAsync("/api/v1/vms/parent/lifecycle", new { action = "start" })
                : owner.PutAsJsonAsync($"/api/v1/vms/parent/{resource}", resource == "cpu"
                    ? new Dictionary<string, int> { ["cpus"] = 2 } : new() { ["ramGb"] = 4 });
            await Task.WhenAny(request, Task.Delay(100));
            Assert.False(request.IsCompleted);
        }
        using var response = await request.WaitAsync(TimeSpan.FromSeconds(10));
        response.EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("memory")]
    public async Task ApplyingToOffVmReportsDriverFailureAndKeepsDesiredSetting(string resource)
    {
        using var app = new TestApp();
        var ledger = app.Service<InMemoryCapacityLedger>();
        ledger.Inventory = ledger.Inventory with { Complete = true, CpuLogical = 8, RamTotalBytes = 16L << 30, RamAvailableBytes = 12L << 30 };
        using var owner = await app.CreateUserClientAsync("alice");
        var vm = new Vm("parent", "alice", 4, 8, 50, app.Clock.UtcNow, VmState.Off, 2222, null, IdlePolicy.Disabled, []);
        await app.Vms.AddAsync(vm, 5, default);
        app.Driver.SetState(vm.Name, VmState.Off);
        if (resource == "cpu") app.Driver.PowerFailure = new IOException("driver failure");
        else app.Driver.MemoryFailure = new IOException("driver failure");
        var response = await owner.PutAsJsonAsync($"/api/v1/vms/parent/{resource}",
            resource == "cpu" ? new Dictionary<string, int> { ["cpus"] = 2 } : new() { ["ramGb"] = 4 });
        Assert.False(response.IsSuccessStatusCode);
        var current = (await app.Vms.GetAsync(vm.Name, default))!;
        Assert.Equal(4, current.Cpu);
        Assert.Equal(8, current.RamGb);
        if (resource == "cpu") Assert.Equal(2, (await app.Service<PrimaryCpuSettings>().GetAsync(current, default))!.Cpus);
        else Assert.Equal(4, (await app.Service<PrimaryMemorySettings>().GetAsync(current, default))!.RamGb);
        Assert.DoesNotContain("start:parent", app.Driver.Calls);
    }
}
