using Constructd.Api.Contracts;
using Constructd.Api.Hosting;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;
namespace Constructd.Tests.Api;

public sealed class VmResourceUsageTests
{
    [Theory]
    [InlineData(GracefulShutdownOutcome.Completed)]
    [InlineData(GracefulShutdownOutcome.Unavailable)]
    [InlineData(GracefulShutdownOutcome.Timeout)]
    public async Task Primary_shutdown_is_graceful_and_never_forces_off(GracefulShutdownOutcome outcome)
    {
        using var app = new TestApp();
        using var alice = await app.CreateUserClientAsync("alice");
        await alice.CreateVmAsync("work-vm");
        var childDriver = app.Service<FakeChildVmDriver>();
        childDriver.ShutdownOutcome = outcome;
        app.Driver.Calls.Clear();
        var job = await Delegation.LifecycleTests.Finish(app,
            await alice.PostJsonAsync("/api/v1/vms/work-vm/lifecycle", new { action = "shutdown" }));
        Assert.Equal(outcome == GracefulShutdownOutcome.Completed ? JobState.Succeeded : JobState.Failed, job.State);
        Assert.Contains("shutdown:work-vm", childDriver.Calls);
        Assert.DoesNotContain(app.Driver.Calls, call => call.StartsWith("stop:") || call.StartsWith("remove:"));
        Assert.Equal(outcome == GracefulShutdownOutcome.Completed ? VmState.Off : VmState.Running, app.Driver.StateOf("work-vm"));
    }

    private static HypervisorVmInfo Sample(string name = "work-vm") => new(name, "id", VmState.Running, "Running", 2, 8,
        16L << 30, 16L << 30, false, null,
        [new("disk.vhdx", 150L << 30, 40L << 30, null, "C:\\", true)], 0, "C:\\", true, 12, 5L << 30, 3600);

    [Fact]
    public async Task Samples_are_shared_refreshed_and_marked_stale_on_failure()
    {
        var clock = new MutableClock();
        var inventory = new FakeHypervisorInventory();
        inventory.Snapshot = inventory.Snapshot with { ObservedAt = clock.UtcNow, Vms = [Sample(), Sample("other")] };
        using var reader = new VmResourceUsageReader(inventory, clock);
        var first = await reader.ReadAsync("work-vm", default);
        Assert.Equal(12, first.CpuUsagePercent); Assert.Equal(5L << 30, first.MemoryDemandBytes);
        Assert.Equal(40L << 30, first.DiskFileBytes); Assert.False(first.Stale);
        await reader.ReadAsync("other", default); Assert.Equal(1, inventory.Reads);
        clock.Advance(TimeSpan.FromSeconds(10));
        inventory.Snapshot = inventory.Snapshot with { ObservedAt = clock.UtcNow, Vms = [Sample() with { CpuUsagePercent = 36 }] };
        Assert.Equal(36, (await reader.ReadAsync("work-vm", default)).CpuUsagePercent);
        Assert.Equal(2, inventory.Reads);
        clock.Advance(TimeSpan.FromSeconds(10)); inventory.Failure = new Exception("private dependency text");
        var stale = await reader.ReadAsync("work-vm", default);
        Assert.True(stale.Stale); Assert.Equal(36, stale.CpuUsagePercent); Assert.Equal(inventory.Snapshot.ObservedAt, stale.ObservedAt);
        var missing = await reader.ReadAsync("missing", default);
        Assert.Null(missing.CpuUsagePercent); Assert.Null(missing.MemoryAssignedBytes);
    }

    [Fact]
    public async Task Unknown_demand_and_unreadable_disk_are_not_reported_as_zero_usage()
    {
        var clock = new MutableClock(); var inventory = new FakeHypervisorInventory();
        inventory.Snapshot = inventory.Snapshot with { ObservedAt = clock.UtcNow,
            Vms = [Sample() with { MemoryDemandBytes = 0, Complete = false, CpuUsagePercent = null }] };
        using var reader = new VmResourceUsageReader(inventory, clock);
        var sample = await reader.ReadAsync("work-vm", default);
        Assert.Null(sample.MemoryDemandBytes); Assert.Null(sample.DiskFileBytes); Assert.Null(sample.CpuUsagePercent);
        Assert.Equal(16L << 30, sample.MemoryAssignedBytes);
    }

    [Fact]
    public async Task Vm_inventory_exposes_usage_and_shutdown_for_primary_to_owner_only()
    {
        using var app = new TestApp();
        using var alice = await app.CreateUserClientAsync("alice");
        using var bob = await app.CreateUserClientAsync("bob");
        var vm = new Vm("work-vm", "alice", 8, 16, 150, app.Clock.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, []);
        await app.Vms.AddAsync(vm, 5, default);
        var inventory = app.Service<FakeHypervisorInventory>();
        inventory.Snapshot = inventory.Snapshot with { ObservedAt = app.Clock.UtcNow, Vms = [Sample()] };
        var response = await (await alice.GetAsync("/api/v1/vms")).ReadAsync<List<VmResponse>>();
        var row = Assert.Single(response);
        Assert.Equal(12, row.ResourceUsage!.CpuUsagePercent);
        Assert.Contains(ChildAction.Shutdown, row.AllowedActions!);
        Assert.Empty(await (await bob.GetAsync("/api/v1/vms")).ReadAsync<List<VmResponse>>());
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await bob.GetAsync("/api/v1/vms/work-vm")).StatusCode);
    }
}
