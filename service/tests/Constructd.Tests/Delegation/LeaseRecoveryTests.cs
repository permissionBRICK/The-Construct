using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Hosting;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
namespace Constructd.Tests.Delegation;

public sealed class LeaseRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunningIntentCompletesOriginalClockWithoutAnotherStart(bool restart)
    {
        await using var app = new TestApp(); using var client = await LifecycleTests.Setup(app, restart);
        var vm = (await app.Vms.GetAsync("child", default))!;
        var original = vm.Lease!;
        var accepted = app.Clock.UtcNow.AddMinutes(-2);
        var intent = new LifecycleStart.Intent("20m", 1200, original.Version, accepted, [], "operation", restart);
        var key = new OperationKeyRecord("alice", restart ? "restart-start" : "lifecycle-start", "recovery-key", "fingerprint", "child", null,
            OperationKeyState.InFlight, JsonSerializer.Serialize(intent, ApiJson.Options), vm.PowerGeneration, null, accepted);
        await app.Service<IOperationKeyStore>().TryInsertAsync(key, default);
        app.Driver.SetState("child", VmState.Running);
        var starts = app.Driver.Calls.Count(c => c == "start:child");
        await app.Service<IChildLeaseReconciler>().ReconcileAsync(vm, VmState.Running, default);
        Assert.Empty(await app.Service<LeaseSchedulerService>().TickAsync(default));
        vm = (await app.Vms.GetAsync("child", default))!;
        Assert.Equal(restart ? original : original with { RequestedText = "20m", RequestedSeconds = 1200, ActivatedAt = accepted,
            ExpiresAt = accepted.AddMinutes(20), State = LeaseState.Active, Version = original.Version + 1 }, vm.Lease);
        Assert.Equal(starts, app.Driver.Calls.Count(c => c == "start:child"));
        Assert.Equal(OperationKeyState.Completed, (await app.Service<IOperationKeyStore>().GetAsync("alice", key.Kind, key.Key, default))!.State);
    }
    [Theory]
    [InlineData(VmState.Off, false, "intent-expired")]
    [InlineData(VmState.Unknown, false, "vm-state-unknown")]
    [InlineData(VmState.Off, true, "power-state-changed")]
    public async Task ExpiredUnknownAndStaleIntentsNeverBoot(VmState state, bool moved, string expected)
    {
        await using var app = new TestApp(); using var client = await LifecycleTests.Setup(app, false);
        var vm = (await app.Vms.GetAsync("child", default))!;
        var accepted = app.Clock.UtcNow.AddHours(-1);
        var intent = new LifecycleStart.Intent("10m", 600, vm.Lease!.Version, accepted, [], "operation");
        var key = new OperationKeyRecord("alice", "lifecycle-start", "recovery-key", "fingerprint", "child", null,
            OperationKeyState.InFlight, JsonSerializer.Serialize(intent, ApiJson.Options), vm.PowerGeneration, null, accepted);
        await app.Service<IOperationKeyStore>().TryInsertAsync(key, default);
        if (moved) await app.Service<IAdmissionStore>().MutateAsync(null, scope => scope.UpdatePowerStateAsync("child", VmState.Off, vm.PowerGeneration), default);
        app.Driver.SetState("child", state);
        var starts = app.Service<LifecycleStart>();
        if (expected == "intent-expired") Assert.Equal(expected, (await starts.RunAsync(vm, "10m", 600, key, default)).Code);
        else Assert.Equal(expected, (await Assert.ThrowsAsync<LifecycleException>(() => starts.RunAsync(vm, "10m", 600, key, default))).Code);
        Assert.DoesNotContain("start:child", app.Driver.Calls);
    }
    [Fact]
    public async Task SqliteRestartAfterDowntimeDoesNotRenewPersistedDeadline()
    {
        var root = Path.Combine(Path.GetTempPath(), "lease-restart-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            Lease original; FakeHypervisorDriver driver;
            await using (var first = TestApp.WithSqlite(Path.Combine(root, "state.db")))
            {
                using var client = await LifecycleTests.Setup(first);
                original = (await first.Vms.GetAsync("child", default))!.Lease!; driver = first.Driver;
            }
            await using (var second = TestApp.WithSqlite(Path.Combine(root, "state.db"), configureServices: services =>
                { services.AddSingleton(driver); services.AddSingleton<IHypervisorDriver>(driver); }))
            {
                second.Clock.UtcNow = original.ExpiresAt!.Value.AddHours(12);
                using var client = await second.CreateTokenClientAsync("alice");
                var id = Assert.Single(await second.Service<LeaseSchedulerService>().TickAsync(default));
                Assert.Equal(JobState.Succeeded, (await LifecycleTests.Finish(second, id)).State);
                var final = (await second.Vms.GetAsync("child", default))!.Lease!;
                Assert.Equal(original.ExpiresAt, final.ExpiresAt); Assert.Equal(original.ActivatedAt, final.ActivatedAt);
                Assert.Equal(LeaseState.Expired, final.State);
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task SharedSqliteStartChargesOwnersBudgetAndCannotUseCallersAllowance()
    {
        var root = Path.Combine(Path.GetTempPath(), "shared-budget-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            await using var app = TestApp.WithSqlite(Path.Combine(root, "state.db"));
            using var owner = await LifecycleTests.Setup(app, false); using var bob = await app.CreateUserClientAsync("bob");
            (await owner.PutAsJsonAsync("/api/v1/vms/child/sharing", new { scope = "host" })).EnsureSuccessStatusCode();
            await app.Service<IUserAllowanceStore>().SetAllowanceAsync("alice", UserAllowance.Unset with
                { AllowSharing = true, RamBudgetBytes = 256L << 20 }, default);
            await app.Service<IHostConfigStore>().SetAsync("capacity", Constructd.Core.Configuration.HostAdminDefaults.Capacity with { Mode = CapacityMode.Enforce }, "admin", default);
            var response = await bob.PostAsJsonAsync("/api/v1/vms/child/lifecycle", new { action = "start", lifetime = "10m" });
            Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("user", json.GetProperty("scope").GetString()); Assert.Equal("ram", json.GetProperty("resource").GetString());
            Assert.DoesNotContain("start:child", app.Driver.Calls);
            Assert.All((await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations, row => Assert.Equal("alice", row.ScopeOwner));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedRestartRefusesLostCapacityOrPassedDeadlineWithoutRenewal(bool due)
    {
        await using var app = new TestApp(); using var client = await LifecycleTests.Setup(app);
        var vm = (await app.Vms.GetAsync("child", default))!; var lease = vm.Lease!;
        app.Driver.SetState("child", VmState.Off);
        await app.Service<IAdmissionStore>().MutateAsync(null, scope => scope.UpdatePowerStateAsync("child", VmState.Off, vm.PowerGeneration), default);
        vm = (await app.Vms.GetAsync("child", default))!;
        var lines = new ReservationLine[] { new(ReservationResource.Ram, vm.RamBytes, null, null), new(ReservationResource.Cpu, vm.Cpu, null, null) };
        var job = new Job("interrupted-restart", "vm-restart", "child", "alice", JobState.Failed, [], null, null, app.Clock.UtcNow, app.Clock.UtcNow);
        var intent = new LifecycleStart.Intent(null, null, lease.Version, app.Clock.UtcNow, lines, "restart-operation", true);
        await app.Service<IOperationKeyStore>().TryInsertAsync(new("alice", "restart-start", job.Id + ":start", job.Id, "child", job.Id,
            OperationKeyState.InFlight, JsonSerializer.Serialize(intent, ApiJson.Options), vm.PowerGeneration, null, app.Clock.UtcNow), default);
        var ledger = app.Service<InMemoryCapacityLedger>();
        var runtime = (await ledger.SnapshotAsync(false, default)).Reservations.Where(r => r.Resource != ReservationResource.Storage).Select(r => r.Id).ToArray();
        await ledger.ReleaseAsync(runtime, VmState.Off, "orphaned-operation", default);
        if (due) app.Clock.Advance(TimeSpan.FromHours(1));
        else
        {
            ledger.Mode = CapacityMode.Enforce;
            ledger.Inventory = new(1, app.Clock.UtcNow, true, 1L << 30, 0, 0, 0, 1L << 30, 512L << 20, 1, 1, 0, 1,
                [new(@"C:\", 10L << 30, 10L << 30, 0, 0, 10L << 30)], [], []);
            Assert.True((await ledger.TryReserveAsync(new("bob", "other", "competing-operation", lines, TimeSpan.FromHours(2)), default)).Allowed);
        }
        var before = app.Driver.Calls.Count(c => c == "start:child");
        var failure = await Assert.ThrowsAsync<JobFailureException>(() => app.Service<ChildLifecycleJobs>().RunAsync(job, true, null, new Progress<string>(), default));
        Assert.Equal(due ? "lease-due" : "capacity-exhausted", failure.Message);
        Assert.Equal(before, app.Driver.Calls.Count(c => c == "start:child"));
        Assert.Equal(lease, (await app.Vms.GetAsync("child", default))!.Lease);
    }
}
