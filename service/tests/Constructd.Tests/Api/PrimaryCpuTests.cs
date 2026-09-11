using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

public sealed class PrimaryCpuTests
{
    [Fact]
    public async Task SqlitePersistsPendingSettingsAndUpdatesOnlyCpuWithGenerationGuard()
    {
        var directory = Path.Combine(Path.GetTempPath(), "construct-cpu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configuration = new Dictionary<string, string?> {
            ["Constructd:Persistence"] = "Sqlite", ["Constructd:DatabasePath"] = Path.Combine(directory, "test.db") };
        try
        {
            using (var app = new TestApp(configuration))
            {
                await app.AddUserAsync("alice");
                var vm = new Vm("parent", "alice", 4, 8, 50, app.Clock.UtcNow, VmState.Off, 2222, null, IdlePolicy.Disabled, []);
                Assert.Equal(VmAddOutcome.Added, await app.Vms.AddAsync(vm, 5, default));
                await app.Service<PrimaryCpuSettings>().SaveAsync(vm, 6, "alice", default);
                var admission = app.Service<IAdmissionStore>();
                Assert.Equal(AdmissionOutcome.Accepted, (await admission.MutateAsync(null, s => s.UpdatePrimaryCpuAsync("parent", 6, 0), default)).Outcome);
                Assert.NotEqual(AdmissionOutcome.Accepted, (await admission.MutateAsync(null, s => s.UpdatePrimaryCpuAsync("parent", 2, 99), default)).Outcome);
                var changed = (await app.Vms.GetAsync("parent", default))!;
                Assert.Equal(6, changed.Cpu);
                Assert.Equal(vm.RamGb, changed.RamGb);
                Assert.Equal(vm.DiskGb, changed.DiskGb);
                Assert.Equal(vm.SshForwardPort, changed.SshForwardPort);
                Assert.Equal(vm.IdlePolicy, changed.IdlePolicy);
                Assert.Equal(vm.PowerGeneration, changed.PowerGeneration);
            }
            using (var restarted = new TestApp(configuration))
            {
                var vm = (await restarted.Vms.GetAsync("parent", default))!;
                Assert.Equal(6, vm.Cpu);
                Assert.Equal(6, (await restarted.Service<PrimaryCpuSettings>().GetAsync(vm, default))!.Cpus);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static TestApp CreateApp()
    {
        var app = new TestApp();
        var ledger = app.Service<InMemoryCapacityLedger>();
        ledger.Inventory = ledger.Inventory with { Complete = true, CpuLogical = 8 };
        return app;
    }

    private static Task Allow(TestApp app, int? budget) => app.Service<IUserAllowanceStore>()
        .SetAllowanceAsync("alice", UserAllowance.Unset with { CpuBudget = budget }, default);

    [Theory]
    [InlineData(null, 8)]
    [InlineData(6, 6)]
    [InlineData(20, 8)]
    [InlineData(0, 0)]
    public async Task DefaultsUseHostAndAllowance(int? budget, int expected)
    {
        using var app = CreateApp();
        using var owner = await app.CreateUserClientAsync("alice");
        await Allow(app, budget);
        var reply = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vm-defaults");
        Assert.Equal(expected, reply.GetProperty("recommendedCpus").GetInt32());
    }

    [Fact]
    public async Task DefaultsRespectRemainingAllowanceAndHostPerVmLimit()
    {
        using var app = CreateApp();
        using var owner = await app.CreateUserClientAsync("alice");
        await Allow(app, 6);
        await owner.CreateVmAsync("parent");
        var reply = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vm-defaults");
        Assert.Equal(2, reply.GetProperty("recommendedCpus").GetInt32());
        var limits = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/parent/cpu");
        Assert.Equal(6, limits.GetProperty("maximumCpus").GetInt32());
        await app.Service<IHostConfigStore>().SetAsync("capacity", new CapacityConfig(CapacityMode.Enforce, null, 0, null, 1, 30, 60), "test", default);
        reply = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vm-defaults");
        Assert.Equal(1, reply.GetProperty("recommendedCpus").GetInt32());
    }

    [Fact]
    public async Task HostBudgetIncludesOtherAllocationsButCreditsThisVmsCurrentCount()
    {
        using var app = CreateApp();
        using var owner = await app.CreateUserClientAsync("alice");
        var ledger = app.Service<InMemoryCapacityLedger>();
        ledger.Inventory = ledger.Inventory with { CpuBudget = 8, CpuActive = 3, CpuAvailable = 5 };
        await owner.CreateVmAsync("parent");
        var defaults = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vm-defaults");
        Assert.Equal(1, defaults.GetProperty("recommendedCpus").GetInt32());
        var existing = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/parent/cpu");
        Assert.Equal(5, existing.GetProperty("maximumCpus").GetInt32());
    }

    [Fact]
    public async Task PendingChangeKeepsRunningVmThenRestartAppliesAndReservesNewCount()
    {
        using var app = CreateApp();
        using var owner = await app.CreateUserClientAsync("alice");
        await Allow(app, 6);
        await owner.CreateVmAsync("parent");
        (await owner.PutAsJsonAsync("/api/v1/vms/parent/cpu", new { cpus = 6 })).EnsureSuccessStatusCode();
        Assert.Equal(4, (await app.Vms.GetAsync("parent", default))!.Cpu);
        Assert.DoesNotContain(app.Driver.Calls, c => c.StartsWith("cpu:"));
        var row = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/parent");
        Assert.Equal(6, row.GetProperty("pendingCpu").GetInt32());
        var restart = await owner.PostAsJsonAsync("/api/v1/vms/parent/lifecycle", new { action = "restart" });
        restart.EnsureSuccessStatusCode();
        var job = await owner.WaitForJobAsync((await restart.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!);
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(6, app.Driver.CpuCounts["parent"]);
        Assert.Equal(6, (await app.Vms.GetAsync("parent", default))!.Cpu);
        var calls = app.Driver.Calls.ToArray();
        Assert.True(Array.IndexOf(calls, "cpu:parent:6") < Array.LastIndexOf(calls, "start:parent"));
        var snapshot = await app.Service<ICapacityLedger>().SnapshotAsync(false, default);
        Assert.Equal(6, snapshot.Reservations.Where(r => r.VmName == "parent" && r.Resource == ReservationResource.Cpu).Sum(r => r.Amount));
    }

    [Fact]
    public async Task SavedResumeDoesNotApplyAndNextColdStartDoes()
    {
        using var app = CreateApp();
        using var owner = await app.CreateUserClientAsync("alice");
        await owner.CreateVmAsync("parent");
        (await owner.PutAsJsonAsync("/api/v1/vms/parent/cpu", new { cpus = 6 })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync("/api/v1/vms/parent/lifecycle", new { action = "save" })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync("/api/v1/vms/parent/power", new { action = "start" })).EnsureSuccessStatusCode();
        Assert.DoesNotContain(app.Driver.Calls, c => c.StartsWith("cpu:"));
        app.Driver.SetState("parent", VmState.Off);
        (await owner.PostAsJsonAsync("/api/v1/vms/parent/power", new { action = "start" })).EnsureSuccessStatusCode();
        Assert.Equal(6, app.Driver.CpuCounts["parent"]);
    }

    [Fact]
    public async Task SettingRequiresOwnerAndFitsOwnersBudgetEvenForAdmin()
    {
        using var app = CreateApp();
        using var owner = await app.CreateUserClientAsync("alice");
        using var other = await app.CreateUserClientAsync("bob");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await Allow(app, 6);
        var job = await owner.CreateVmAsync("parent");
        using var token = app.CreateVmTokenClient(job.VmToken());
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync("/api/v1/vms/parent/cpu", new { cpus = 6 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.PutAsJsonAsync("/api/v1/vms/parent/cpu", new { cpus = 6 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/vms/parent/cpu", new { cpus = 7 })).StatusCode);
        (await admin.PutAsJsonAsync("/api/v1/vms/parent/cpu", new { cpus = 6 })).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync("/api/v1/vms/parent/cpu", new { cpus = 4 })).EnsureSuccessStatusCode();
        var reply = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/parent/cpu");
        Assert.False(reply.GetProperty("pending").GetBoolean());
    }

    [Fact]
    public async Task ReducedAllowanceBlocksApplicationAndRetainsPendingChange()
    {
        using var app = CreateApp();
        using var owner = await app.CreateUserClientAsync("alice");
        await owner.CreateVmAsync("parent");
        (await owner.PutAsJsonAsync("/api/v1/vms/parent/cpu", new { cpus = 6 })).EnsureSuccessStatusCode();
        await Allow(app, 4);
        app.Driver.SetState("parent", VmState.Off);
        var response = await owner.PostAsJsonAsync("/api/v1/vms/parent/power", new { action = "start" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("cpu-allowance-exceeded", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(app.Driver.Calls, c => c.StartsWith("cpu:"));
        Assert.Equal(VmState.Off, app.Driver.StateOf("parent"));
        Assert.Equal(6, (await app.Service<PrimaryCpuSettings>().GetAsync((await app.Vms.GetAsync("parent", default))!, default))!.Cpus);
    }

    [Fact]
    public async Task IncompleteHostInventoryNeverGuesses()
    {
        using var app = CreateApp();
        using var owner = await app.CreateUserClientAsync("alice");
        var ledger = app.Service<InMemoryCapacityLedger>();
        ledger.Inventory = ledger.Inventory with { Complete = false };
        Assert.Equal(HttpStatusCode.Conflict, (await owner.GetAsync("/api/v1/vm-defaults")).StatusCode);
    }

    [Fact]
    public async Task HypervisorFailureRetainsSettingAndNeverStarts()
    {
        using var app = CreateApp();
        using var owner = await app.CreateUserClientAsync("alice");
        await owner.CreateVmAsync("parent");
        (await owner.PutAsJsonAsync("/api/v1/vms/parent/cpu", new { cpus = 6 })).EnsureSuccessStatusCode();
        app.Driver.SetState("parent", VmState.Off);
        app.Driver.PowerFailure = new IOException("driver failure");
        var response = await owner.PostAsJsonAsync("/api/v1/vms/parent/power", new { action = "start" });
        Assert.False(response.IsSuccessStatusCode);
        Assert.DoesNotContain("start:parent", app.Driver.Calls);
        Assert.Equal(4, (await app.Vms.GetAsync("parent", default))!.Cpu);
        app.Driver.PowerFailure = null;
        (await owner.PostAsJsonAsync("/api/v1/vms/parent/power", new { action = "start" })).EnsureSuccessStatusCode();
        Assert.Equal(6, app.Driver.CpuCounts["parent"]);
    }

    [Fact]
    public async Task RecreatedVmDoesNotInheritOldPendingSettings()
    {
        using var app = CreateApp();
        using var owner = await app.CreateUserClientAsync("alice");
        await owner.CreateVmAsync("parent");
        var vm = (await app.Vms.GetAsync("parent", default))!;
        var settings = app.Service<PrimaryCpuSettings>();
        await settings.SaveAsync(vm, 6, "alice", default);
        Assert.Null(await settings.GetAsync(vm with { Created = vm.Created.AddSeconds(1) }, default));
    }
}
