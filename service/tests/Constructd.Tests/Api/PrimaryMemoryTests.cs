using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

public sealed class PrimaryMemoryTests
{
    private const long Gib = 1L << 30;
    private static TestApp App()
    {
        var app = new TestApp();
        var ledger = app.Service<InMemoryCapacityLedger>();
        ledger.Inventory = ledger.Inventory with { Complete = true, CpuLogical = 8, RamTotalBytes = 32 * Gib,
            RamPhysicalFreeBytes = 28 * Gib, RamHeadroomBytes = 4 * Gib, RamAvailableBytes = 28 * Gib };
        return app;
    }
    private static Task Allow(TestApp app, long budget) => app.Service<IUserAllowanceStore>()
        .SetAllowanceAsync("alice", UserAllowance.Unset with { RamBudgetBytes = budget }, default);

    [Theory]
    [InlineData(12)]
    [InlineData(4)]
    public async Task RestartAppliesCpuAndRamAndReplacesExactlyTheNewLiabilities(int ramGb)
    {
        using var app = App(); using var owner = await app.CreateUserClientAsync("alice");
        await owner.CreateVmAsync("parent");
        await Allow(app, 16 * Gib);
        var before = await app.Service<ICapacityLedger>().SnapshotAsync(false, default);
        (await owner.PutAsJsonAsync("/api/v1/vms/parent/memory", new { ramGb })).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync("/api/v1/vms/parent/cpu", new { cpus = 6 })).EnsureSuccessStatusCode();
        Assert.Equal(8, (await app.Vms.GetAsync("parent", default))!.RamGb);
        Assert.Equal(before.Reservations, (await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations);
        var row = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/parent");
        Assert.Equal(ramGb, row.GetProperty("pendingRamGb").GetInt32());
        var response = await owner.PostAsJsonAsync("/api/v1/vms/parent/lifecycle", new { action = "restart" });
        response.EnsureSuccessStatusCode();
        var job = await owner.WaitForJobAsync((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!);
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(ramGb, app.Driver.MemorySizes["parent"]);
        var calls = app.Driver.Calls.ToArray();
        Assert.True(Array.IndexOf(calls, "cpu:parent:6") < Array.IndexOf(calls, $"memory:parent:{ramGb}"));
        Assert.True(Array.IndexOf(calls, $"memory:parent:{ramGb}") < Array.LastIndexOf(calls, "start:parent"));
        var rows = (await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations.Where(r => r.VmName == "parent").ToArray();
        Assert.Equal(ramGb * Gib, rows.Where(r => r.Resource == ReservationResource.Ram).Sum(r => r.Amount));
        Assert.Equal(6, rows.Where(r => r.Resource == ReservationResource.Cpu).Sum(r => r.Amount));
        Assert.Equal(ramGb * Gib + CapacityMath.SavedStateOverhead, rows.Where(ReservationRules.SavedState).Sum(r => r.Amount));
    }

    [Fact]
    public async Task OwnerAdminAndTokenAuthorizationAndCapsAreAppliedPerRequest()
    {
        using var app = App(); using var owner = await app.CreateUserClientAsync("alice");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        using var other = await app.CreateUserClientAsync("other");
        var job = await owner.CreateVmAsync("parent"); using var token = app.CreateVmTokenClient(job.VmToken());
        await Allow(app, 12 * Gib);
        foreach (var client in new[] { other, token })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/vms/parent/memory")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/v1/vms/parent/memory", new { ramGb = 12 })).StatusCode);
        }
        foreach (var bad in new[] { 0, -1, 13, 1025 })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/vms/parent/memory", new { ramGb = bad })).StatusCode);
        (await admin.PutAsJsonAsync("/api/v1/vms/parent/memory", new { ramGb = 12 })).EnsureSuccessStatusCode();
        var reply = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/parent/memory");
        Assert.Equal(12, reply.GetProperty("maximumRamGb").GetInt32());
        await Allow(app, 8 * Gib);
        app.Driver.SetState("parent", VmState.Off);
        var start = await owner.PostAsJsonAsync("/api/v1/vms/parent/power", new { action = "start" });
        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        Assert.Contains("memoryAllowanceExceeded", await start.Content.ReadAsStringAsync());
        Assert.Empty(app.Driver.MemorySizes);
        var audit = await admin.GetFromJsonAsync<JsonElement>("/api/v1/audit");
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "vm.memory");
    }

    [Fact]
    public async Task SavedResumeLeavesDesiredRamPendingAndDriverFailureKeepsActualCpu()
    {
        using var app = App(); using var owner = await app.CreateUserClientAsync("alice");
        await owner.CreateVmAsync("parent");
        (await owner.PutAsJsonAsync("/api/v1/vms/parent/memory", new { ramGb = 12 })).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync("/api/v1/vms/parent/cpu", new { cpus = 6 })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync("/api/v1/vms/parent/lifecycle", new { action = "save" })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync("/api/v1/vms/parent/power", new { action = "start" })).EnsureSuccessStatusCode();
        Assert.Empty(app.Driver.MemorySizes);
        app.Driver.SetState("parent", VmState.Off);
        app.Driver.MemoryFailure = new IOException("private driver failure");
        Assert.False((await owner.PostAsJsonAsync("/api/v1/vms/parent/power", new { action = "start" })).IsSuccessStatusCode);
        var vm = (await app.Vms.GetAsync("parent", default))!;
        Assert.Equal(6, vm.Cpu); Assert.Equal(8, vm.RamGb);
        Assert.Equal(VmState.Off, app.Driver.StateOf("parent"));
        Assert.Equal(12, (await app.Service<PrimaryMemorySettings>().GetAsync(vm, default))!.RamGb);
        app.Driver.MemoryFailure = null;
        (await owner.PostAsJsonAsync("/api/v1/vms/parent/power", new { action = "start" })).EnsureSuccessStatusCode();
        Assert.Equal(12, (await app.Vms.GetAsync("parent", default))!.RamGb);
    }

    [Fact]
    public async Task SqlitePersistsDesiredMemoryAndGuardsFieldOnlyUpdates()
    {
        var path = Path.Combine(Path.GetTempPath(), "construct-memory-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        var config = new Dictionary<string, string?> { ["Constructd:Persistence"] = "Sqlite", ["Constructd:DatabasePath"] = Path.Combine(path, "test.db") };
        try
        {
            using (var app = new TestApp(config))
            {
                await app.AddUserAsync("alice");
                var vm = new Vm("parent", "alice", 4, 8, 50, app.Clock.UtcNow, VmState.Off, 2222, null, IdlePolicy.Disabled, []);
                await app.Vms.AddAsync(vm, 5, default);
                await app.Service<PrimaryMemorySettings>().SaveAsync(vm, 12, "alice", default);
                var admission = app.Service<IAdmissionStore>();
                Assert.Equal(AdmissionOutcome.Accepted, (await admission.MutateAsync(null, s => s.UpdatePrimaryRamAsync(vm.Name, 12, 0), default)).Outcome);
                Assert.NotEqual(AdmissionOutcome.Accepted, (await admission.MutateAsync(null, s => s.UpdatePrimaryRamAsync(vm.Name, 1, 99), default)).Outcome);
                Assert.Equal(AdmissionOutcome.Accepted, (await admission.MutateAsync(null, s => s.UpdatePrimaryIdleAsync(vm.Name, new(60, IdleAction.Shutdown), 0), default)).Outcome);
                var changed = (await app.Vms.GetAsync(vm.Name, default))!;
                Assert.Equal(12, changed.RamGb); Assert.Equal(vm.Cpu, changed.Cpu); Assert.Equal(vm.DiskGb, changed.DiskGb);
                Assert.Equal(vm.SshForwardPort, changed.SshForwardPort); Assert.Equal(60, changed.IdlePolicy.TimeoutMinutes);
                Assert.Null(await app.Service<PrimaryMemorySettings>().GetAsync(vm with { Created = vm.Created.AddSeconds(1) }, default));
            }
            using var reopened = new TestApp(config);
            var persisted = (await reopened.Vms.GetAsync("parent", default))!;
            Assert.Equal(12, persisted.RamGb);
            Assert.Equal(12, (await reopened.Service<PrimaryMemorySettings>().GetAsync(persisted, default))!.RamGb);
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public async Task RefusedSavedStateGrowthRollsBackReplacementAndNeverStarts()
    {
        using var app = App(); using var owner = await app.CreateUserClientAsync("alice");
        await owner.CreateVmAsync("parent");
        var ledger = app.Service<InMemoryCapacityLedger>();
        var before = await ledger.SnapshotAsync(false, default);
        (await owner.PutAsJsonAsync("/api/v1/vms/parent/memory", new { ramGb = 12 })).EnsureSuccessStatusCode();
        ledger.Mode = CapacityMode.Enforce;
        // Storage is exhausted even though RAM fits. Admission must undo releases.
        ledger.Inventory = ledger.Inventory with { Volumes = [] };
        app.Driver.SetState("parent", VmState.Off);
        var vm = (await app.Vms.GetAsync("parent", default))!;
        await using var gate = await app.Service<IVmOperationGate>().TryAcquireAsync(vm.Name, "test", default);
        var key = new OperationKeyRecord("alice", "restart-start", "key", "fingerprint", vm.Name, null, OperationKeyState.InFlight, null, vm.PowerGeneration, null, app.Clock.UtcNow);
        var ex = await Assert.ThrowsAsync<LifecycleException>(() => app.Service<LifecycleStart>().RunAsync(vm, null, null, key, default));
        Assert.Equal("capacity-exhausted", ex.Code);
        Assert.Equal(before.Reservations, (await ledger.SnapshotAsync(false, default)).Reservations);
        Assert.Equal(12, (await app.Vms.GetAsync(vm.Name, default))!.RamGb);
        Assert.DoesNotContain("start:parent", app.Driver.Calls);
        Assert.Null(await app.Service<IOperationKeyStore>().GetAsync("alice", "restart-start", "key", default));
    }

    [Fact]
    public async Task ConcurrentResizesCannotSpendTheSameHostRam()
    {
        using var app = App(); using var owner = await app.CreateUserClientAsync("alice");
        await owner.CreateVmAsync("first"); await owner.CreateVmAsync("second");
        var ledger = app.Service<InMemoryCapacityLedger>();
        ledger.Inventory = ledger.Inventory with { RamAvailableBytes = 20 * Gib, Volumes = [new(@"C:\", 1000 * Gib, 900 * Gib, 0, 0, 900 * Gib)] };
        ledger.Mode = CapacityMode.Enforce;
        foreach (var name in new[] { "first", "second" })
            (await owner.PutAsJsonAsync($"/api/v1/vms/{name}/memory", new { ramGb = 12 })).EnsureSuccessStatusCode();
        async Task<Constructd.Api.Contracts.JobResponse> Restart(string name)
        {
            var response = await owner.PostAsJsonAsync($"/api/v1/vms/{name}/lifecycle", new { action = "restart" });
            response.EnsureSuccessStatusCode();
            return await owner.WaitForJobAsync((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!);
        }
        var jobs = await Task.WhenAll(Restart("first"), Restart("second"));
        Assert.Single(jobs, job => job.State == JobState.Succeeded);
        Assert.True((await ledger.SnapshotAsync(false, default)).Reservations.Where(r => r.Resource == ReservationResource.Ram).Sum(r => r.Amount) <= 20 * Gib);
    }

    [Fact]
    public async Task AdminIdlePolicyStillClampsAndAdvertisesForcedIdle()
    {
        using var app = new TestApp(new Dictionary<string, string?> { ["Constructd:Idle:MaxTimeoutMinutes"] = "60", ["Constructd:Idle:ForceEnabled"] = "true" });
        using var owner = await app.CreateUserClientAsync("alice"); using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await owner.CreateVmAsync("parent");
        var result = await admin.PutAsJsonAsync("/api/v1/vms/parent/idle-policy", new { timeoutMinutes = 0, action = "off" });
        result.EnsureSuccessStatusCode();
        var reply = await result.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(reply.GetProperty("forceEnabled").GetBoolean());
        Assert.True(reply.GetProperty("clamped").GetBoolean());
        Assert.Equal(60, reply.GetProperty("timeoutMinutes").GetInt32());
        Assert.Equal("save", reply.GetProperty("action").GetString());
    }

    [Theory]
    [InlineData(VmState.Running, 8, 20)]
    [InlineData(VmState.Running, 4, 16)]
    [InlineData(VmState.Off, 0, 12)]
    public void ReclaimableBoundCreditsAssignedAndUnreflectedRamFromOneEpoch(VmState state, int assigned, int expected)
    {
        var vm = new Vm("parent", "alice", 4, 8, 50, DateTimeOffset.UtcNow, state, null, null, IdlePolicy.Disabled, []);
        var actual = new HypervisorVmInfo(vm.Name, "id", state, state.ToString(), 2, 4, 8 * Gib, assigned * Gib, false, null, [], 0, "C:", true);
        var inventory = new InventorySnapshot(1, vm.Created, new(8, 32 * Gib, 16 * Gib, [new("C:", 1000 * Gib, 900 * Gib)], vm.Created), [actual], true, []);
        var row = new Reservation("ram", ReservationResource.Ram, "alice", "parent", null, null, 8 * Gib, ReservationPhase.Held, ReservationOrigin.Api, "op", vm.Created, null, vm.Created);
        var snapshot = CapacityMath.Calculate(inventory, new(CapacityMode.Enforce, 4 * Gib, 0, null, null, 60, 600), [row], [vm]);
        Assert.Equal(expected * Gib, snapshot.RamAvailableByVm![vm.Name]);
    }
}
