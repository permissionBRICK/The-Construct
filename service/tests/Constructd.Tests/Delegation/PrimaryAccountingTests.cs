using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Microsoft.Data.Sqlite;
namespace Constructd.Tests.Delegation;

public sealed class PrimaryAccountingTests
{
    private static object Primary(string name) => new { name, cpu = 1, ramGb = 1, diskGb = 8 };
    private static HostCapacitySnapshot Inventory(long ram) => new(1, DateTimeOffset.UtcNow, true, 8L << 30, 0, 0, 0, 8L << 30, ram, 8, 8, 0, 8,
        [new(@"C:\", 64L << 30, 64L << 30, 0, 0, 64L << 30)], [], []);
    [Fact]
    public async Task PrimaryCreateAndResumeUseTheSharedLedger()
    {
        await using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice");
        var ledger = app.Service<InMemoryCapacityLedger>(); ledger.Mode = CapacityMode.Enforce; ledger.Inventory = Inventory(512L << 20);
        var refused = await owner.PostAsJsonAsync("/api/v1/vms", Primary("primary"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode); Assert.Contains("capacity-exhausted", await refused.Content.ReadAsStringAsync());
        Assert.Null(await app.Vms.GetAsync("primary", default)); Assert.Empty((await ledger.SnapshotAsync(false, default)).Reservations);
        ledger.Inventory = Inventory(2L << 30);
        Assert.Equal(JobState.Succeeded, (await LifecycleTests.Finish(app, await owner.PostAsJsonAsync("/api/v1/vms", Primary("primary")))).State);
        var held = (await ledger.SnapshotAsync(false, default)).Reservations;
        Assert.Equal(4, held.Count); Assert.All(held, r => Assert.Equal("alice", r.ScopeOwner)); Assert.All(held, r => Assert.Equal(ReservationPhase.Held, r.Phase));
        (await owner.PostAsJsonAsync("/api/v1/vms/primary/power", new { action = "stop" })).EnsureSuccessStatusCode();
        Assert.StartsWith("disk:", Assert.Single((await ledger.SnapshotAsync(false, default)).Reservations).Artifact);
        Assert.True((await ledger.TryReserveAsync(new("bob", "other", "other-operation", [new(ReservationResource.Ram, 2L << 30, null, null)], TimeSpan.FromHours(1)), default)).Allowed);
        var starts = app.Driver.Calls.Count(c => c == "start:primary");
        var denied = await owner.PostAsJsonAsync("/api/v1/vms/primary/power", new { action = "start" });
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode); Assert.Equal(starts, app.Driver.Calls.Count(c => c == "start:primary"));
        Assert.Equal(VmState.Off, app.Driver.StateOf("primary"));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedPrimaryCreateRetryAdoptsExactlyOneOwnDiskLiability(bool sqlite, bool smaller)
    {
        var root = Path.Combine(Path.GetTempPath(), "primary-retry-" + Guid.NewGuid().ToString("n")); Directory.CreateDirectory(root);
        try
        {
            await using var app = sqlite ? TestApp.WithSqlite(Path.Combine(root, "state.db")) : new TestApp();
            using var owner = await app.CreateUserClientAsync("alice"); app.Driver.Reachable = false;
            Assert.Equal(JobState.Failed, (await LifecycleTests.Finish(app, await owner.PostAsJsonAsync("/api/v1/vms", new { name = "primary", cpu = 1, ramGb = 1, diskGb = smaller ? 16 : 8 }))).State);
            Assert.Null(await app.Vms.GetAsync("primary", default));
            Assert.StartsWith("disk:", Assert.Single((await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations).Artifact);
            app.Driver.Reachable = true;
            var retry = await LifecycleTests.Finish(app, await owner.PostAsJsonAsync("/api/v1/vms", new { name = "primary", cpu = 1, ramGb = 1, diskGb = 8 }));
            Assert.Equal(JobState.Succeeded, retry.State);
            var disk = Assert.Single((await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations, r => r.Artifact?.StartsWith("disk:", StringComparison.Ordinal) == true);
            Assert.Equal("alice", disk.ScopeOwner); Assert.Equal(retry.Id, disk.OperationId); Assert.Equal((smaller ? 16L : 8L) << 30, disk.Amount);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedRetryRollsBackAdoptionOfTheFailedCreatesDisk(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "adoption-rollback-" + Guid.NewGuid().ToString("n")); Directory.CreateDirectory(root);
        try
        {
            await using var app = sqlite ? TestApp.WithSqlite(Path.Combine(root, "state.db")) : new TestApp();
            using var owner = await app.CreateUserClientAsync("alice"); app.Driver.Reachable = false;
            var failed = await LifecycleTests.Finish(app, await owner.PostAsJsonAsync("/api/v1/vms", Primary("primary")));
            Assert.Equal(JobState.Failed, failed.State);
            var capacity = app.Service<ICapacityLedger>(); var original = Assert.Single((await capacity.SnapshotAsync(false, default)).Reservations);
            if (sqlite) await app.Service<IHostConfigStore>().SetAsync("capacity", Constructd.Core.Configuration.HostAdminDefaults.Capacity with { Mode = CapacityMode.Enforce }, "admin", default);
            else app.Service<InMemoryCapacityLedger>().Mode = CapacityMode.Enforce;
            app.Driver.Reachable = true;
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/v1/vms", Primary("primary"))).StatusCode);
            Assert.Equal(original, Assert.Single((await capacity.SnapshotAsync(false, default)).Reservations));
            Assert.Equal(failed.Id, original.OperationId); Assert.Null(await app.Vms.GetAsync("primary", default));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(CapacityMode.Observe, true)]
    [InlineData(CapacityMode.Enforce, true)]
    [InlineData(CapacityMode.Observe, false)]
    [InlineData(CapacityMode.Enforce, false)]
    public async Task UnknownStateRefusesPrimaryCreateAndStartBeforeAnyDriverMutation(CapacityMode mode, bool create)
    {
        await using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice");
        app.Service<InMemoryCapacityLedger>().Mode = mode;
        if (!create) await app.Vms.AddAsync(new("primary", "alice", 1, 1, 8, app.Clock.UtcNow, VmState.Off, null, null, IdlePolicy.Disabled, []), 5, default);
        app.Driver.SetState("primary", VmState.Unknown);
        var result = create ? await owner.PostAsJsonAsync("/api/v1/vms", Primary("primary")) :
            await owner.PostAsJsonAsync("/api/v1/vms/primary/power", new { action = "start" });
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode); Assert.Contains("vm-state-unknown", await result.Content.ReadAsStringAsync());
        Assert.DoesNotContain("create:primary", app.Driver.Calls); Assert.DoesNotContain("start:primary", app.Driver.Calls); Assert.DoesNotContain("remove:primary", app.Driver.Calls);
    }
    [Fact]
    public async Task ForeignRetainedDiskReturnsCodedConflictAndCannotBeAdopted()
    {
        var root = Path.Combine(Path.GetTempPath(), "primary-conflict-" + Guid.NewGuid().ToString("n")); Directory.CreateDirectory(root);
        try
        {
            await using var app = TestApp.WithSqlite(Path.Combine(root, "state.db")); using var owner = await app.CreateUserClientAsync("alice");
            var capacity = app.Service<ICapacityLedger>();
            var foreign = new ReservationRequest("bob", "primary", "foreign-create", [new(ReservationResource.Storage, 8L << 30, @"disk:C:\VMs\primary.vhdx", @"C:\")], TimeSpan.FromHours(2));
            Assert.True((await capacity.TryReserveAsync(foreign, default)).Allowed);
            var result = await owner.PostAsJsonAsync("/api/v1/vms", Primary("primary"));
            Assert.Equal(HttpStatusCode.Conflict, result.StatusCode); Assert.Contains("operation-in-progress", await result.Content.ReadAsStringAsync());
            Assert.Null(await app.Vms.GetAsync("primary", default));
            Assert.Equal("bob", Assert.Single((await capacity.SnapshotAsync(false, default)).Reservations).ScopeOwner);
            CapacityDecision? refusal = null;
            await app.Service<IAdmissionStore>().MutateAsync(null, async scope =>
            {
                refusal = await scope.ReserveAsync(foreign with { Owner = "alice", OperationId = "retry" }); return refusal.Allowed;
            }, default);
            Assert.False(refusal!.Allowed); Assert.Equal("reservation-conflict", refusal.Reason);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task PrimaryCreateKeyReplayDoesNotCreateOrAllocateTwice()
    {
        await using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice");
        owner.DefaultRequestHeaders.Add("X-Construct-Operation-Key", "primary-create-replay");
        var first = await LifecycleTests.Finish(app, await owner.PostAsJsonAsync("/api/v1/vms", Primary("primary")));
        Assert.Equal(JobState.Succeeded, first.State);
        var before = (await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations.Count;
        var replay = await owner.PostAsJsonAsync("/api/v1/vms", Primary("primary"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(first.Id, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString());
        Assert.Equal(before, (await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations.Count);
        Assert.Single(app.Driver.Calls, c => c == "create:primary");
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/v1/vms", Primary("other"))).StatusCode);
    }
    [Fact]
    public async Task PrimaryCreateCannotRollBackAnExistingUnmanagedVm()
    {
        await using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice");
        app.Driver.SetState("unmanaged", VmState.Running);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/v1/vms", Primary("unmanaged"))).StatusCode);
        Assert.Equal(VmState.Running, app.Driver.StateOf("unmanaged"));
        Assert.DoesNotContain("remove:unmanaged", app.Driver.Calls); Assert.DoesNotContain("create:unmanaged", app.Driver.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PerParentLimitAndOwnerAggregateAreBothAtomic(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "child-counts-" + Guid.NewGuid().ToString("n")); Directory.CreateDirectory(root);
        try
        {
            await using var app = sqlite ? TestApp.WithSqlite(Path.Combine(root, "state.db")) : new TestApp();
            using var client = await LifecycleTests.Setup(app, false);
            (await client.PutAsJsonAsync("/api/v1/vms/child/sharing", new { scope = "host" })).EnsureSuccessStatusCode();
            foreach (var parent in new[] { "second", "third" })
                await app.Vms.AddAsync(new(parent, "alice", 1, 1, 1, app.Clock.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, []), 5, default);
            await app.Service<IVmDelegationRepository>().SetOverrideAsync(new("second", null, 1, null, null, null, app.Clock.UtcNow), default);
            Assert.Equal(JobState.Succeeded, (await LifecycleTests.Finish(app, await client.PostAsJsonAsync("/api/v1/vms/second/children", LifecycleTests.Request("second-child")))).State);
            var parentRefused = await client.PostAsJsonAsync("/api/v1/vms/second/children", LifecycleTests.Request("too-many-for-parent"));
            Assert.Equal(HttpStatusCode.Conflict, parentRefused.StatusCode);
            var detail = await parentRefused.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("children", detail.GetProperty("resource").GetString());
            Assert.Equal("parent-child-limit", detail.GetProperty("reason").GetString());
            Assert.Equal(1, detail.GetProperty("allowed").GetInt32());
            await app.Service<IUserAllowanceStore>().SetAllowanceAsync("alice", UserAllowance.Unset with { AllowChildCreation = true, MaxRetainedChildren = 2 }, default);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/vms/third/children", LifecycleTests.Request("too-many-for-owner"))).StatusCode);
            Assert.Equal(2, await app.Service<IVmDelegationRepository>().CountByOwnerAsync("alice", VmKind.Child, default));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task PrimaryCreationCannotBypassChildOwnersSqliteBudget()
    {
        var root = Path.Combine(Path.GetTempPath(), "primary-budget-" + Guid.NewGuid().ToString("n")); Directory.CreateDirectory(root);
        try
        {
            await using var app = TestApp.WithSqlite(Path.Combine(root, "state.db")); using var owner = await LifecycleTests.Setup(app);
            await app.Service<IUserAllowanceStore>().SetAllowanceAsync("alice", UserAllowance.Unset with { RamBudgetBytes = 512L << 20 }, default);
            await app.Service<IHostConfigStore>().SetAsync("capacity", Constructd.Core.Configuration.HostAdminDefaults.Capacity with { Mode = CapacityMode.Enforce }, "admin", default);
            var refused = await owner.PostAsJsonAsync("/api/v1/vms", Primary("another-primary"));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var json = await refused.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("user", json.GetProperty("scope").GetString()); Assert.Equal("ram", json.GetProperty("resource").GetString());
            Assert.Null(await app.Vms.GetAsync("another-primary", default)); Assert.DoesNotContain("create:another-primary", app.Driver.Calls);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
