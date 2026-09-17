using Constructd.Api.Hosting;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Constructd.Tests.Core;

public class MemoryPressureEngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private const long Gb = 1L << 30;
    private sealed class Harness
    {
        public MutableClock Clock { get; } = new(Start);
        public InMemoryVmRepository Vms { get; } = new();
        public FakeHypervisorDriver Driver { get; } = new();
        public InMemoryAuditLog Audit { get; } = new();
        public InMemoryJobStore Jobs { get; } = new();
        public InMemoryHostConfigStore Config { get; }
        public InMemoryCapacityLedger Capacity { get; }
        public InMemoryVmOperationGate Gate { get; } = new();
        public InMemoryPortForwardManager Forwards { get; }
        public MemoryPressureState Status { get; } = new();
        public IdleOptions Options { get; } = new() { ReportIntervalMinutes = 1, MissingReportGraceMultiple = 3 };
        public IdlePolicyEngine Engine { get; }
        public List<HypervisorVmInfo> Actual { get; } = [];
        public Harness(bool proxmox = false, Func<IVmOperationGate, IVmOperationGate>? gate = null)
        {
            Config = new(Clock);
            Capacity = new(Clock);
            Forwards = new(Clock, Vms, new InMemoryForwardStore(), new(2201, 2299), new(2300, 2999));
            Engine = new(Vms, Forwards, Driver, Audit, Options, gate?.Invoke(Gate) ?? Gate,
                new(Config, Capacity, Jobs, Vms, Clock, Status, proxmox));
        }
        public async Task Add(string name, int timeout = 60, VmKind kind = VmKind.Primary, string? parent = null)
        {
            await Vms.AddAsync(new(name, "owner", 2, 10, 20, Start.AddDays(-1), VmState.Running, null, null,
                new(timeout, IdleAction.Save), [], Kind: kind, Parent: parent), 20, default);
            Driver.SetState(name, VmState.Running);
            await Vms.SaveActivityAsync(new(name, false, [], Start), default);
            Actual.Add(new(name, name, VmState.Running, "running", 2, 2, 10 * Gb, 10 * Gb, false, null, [], null, "/", true,
                MemoryDemandBytes: 2 * Gb));
        }
        public void Sample(int used = 95, bool complete = true)
        {
            Capacity.Inventory = new(Capacity.Inventory.Epoch + 1, Clock.UtcNow, complete, 100 * Gb, 0, 0, 0,
                (100 - used) * Gb, (100 - used) * Gb, 8, null, 0, null, [], [], [],
                RamUsedBytes: used * Gb, MeasuredVms: Actual.ToArray());
        }
        public async Task Tick(int minute, bool measure = true, int used = 95)
        {
            Clock.UtcNow = Start.AddMinutes(minute);
            if (measure) Sample(used);
            await Engine.EvaluateAsync(Clock.UtcNow, default);
        }
        public string[] Saves => Driver.Calls.Where(c => c.StartsWith("save:")).ToArray();
        public Task Job(string vm, JobState state = JobState.Running) => Jobs.UpsertAsync(
            new("job-" + vm, "provision", vm, "owner", state, [], null, null, Clock.UtcNow, null), default);
    }

    [Fact]
    public async Task Saves_closest_idle_vm_then_requires_new_measurement_and_continues_to_low_water()
    {
        var h = new Harness();
        await h.Add("a", 90); await h.Add("b", 60); await h.Add("c", 120);
        await h.Tick(0);
        Assert.Empty(h.Saves);
        await h.Tick(20);
        Assert.Equal(new[] { "save:b" }, h.Saves);
        var saved = (await h.Vms.GetAsync("b", default))!;
        Assert.Equal(VmState.Saved, saved.State);
        Assert.Equal("memory-pressure", saved.SavedBy);
        Assert.Equal(h.Clock.UtcNow, saved.PressureSavedAt);
        var audit = Assert.Single(await h.Audit.QueryAsync(20, default), e => e.Action == "vm.pressure-save");
        Assert.Contains("95%", audit.Detail);
        Assert.Contains("40 min", audit.Detail);
        await h.Tick(21, measure: false);
        Assert.Single(h.Saves);
        Assert.Equal("waiting-for-measurement", h.Status.Status.State);
        await h.Tick(21, used: 85);
        Assert.Equal(new[] { "save:b", "save:a" }, h.Saves);
        await h.Tick(22, used: 79);
        Assert.Equal(2, h.Saves.Length);
        Assert.Equal("idle", h.Status.Status.State);
        Assert.DoesNotContain(h.Driver.Calls, c => c.StartsWith("stop:"));
        Assert.DoesNotContain(await h.Audit.QueryAsync(20, default), e => e.Action == "vm.idle-save");
    }

    [Theory]
    [InlineData("connections")]
    [InlineData("heartbeat")]
    [InlineData("job")]
    [InlineData("queued-job")]
    [InlineData("child-job")]
    [InlineData("policy-off")]
    [InlineData("gate")]
    public async Task Busy_or_exempt_vms_are_never_saved(string blocker)
    {
        var h = new Harness(); await h.Add("parent"); await h.Tick(0);
        switch (blocker)
        {
            case "connections": h.Forwards.SetActiveConnections("parent", 1); break;
            case "heartbeat": await h.Vms.SaveActivityAsync(new("parent", true, ["provisioning"], Start.AddMinutes(20)), default); break;
            case "job": await h.Job("parent"); break;
            case "queued-job": await h.Job("parent", JobState.Queued); break;
            case "child-job": await h.Add("child", kind: VmKind.Child, parent: "parent"); await h.Job("child"); break;
            case "policy-off":
                var vm = (await h.Vms.GetAsync("parent", default))!;
                await h.Vms.UpdateAsync(vm with { IdlePolicy = IdlePolicy.Disabled }, default); break;
        }
        await using var held = blocker == "gate" ? await h.Gate.AcquireAsync("parent", "provision", default) : null;
        await h.Tick(20);
        Assert.Empty(h.Saves);
        Assert.Equal("insufficient-candidates", h.Status.Status.State);
    }

    [Fact]
    public async Task Disabled_policy_does_not_read_inventory_and_global_scheduler_off_does_not_act()
    {
        var h = new Harness(); await h.Add("vm");
        h.Capacity.ReadInventory = () => throw new Exception("Must not read disabled inventory");
        await h.Config.SetAsync("memoryPressure", new MemoryPressureConfig(Enabled: false), "admin", default);
        await h.Tick(20);
        Assert.False(h.Status.Status.Enabled);
        Assert.Equal("off", h.Status.Status.State);
        Assert.Empty(h.Saves);
        var scheduler = new IdleSchedulerService(h.Engine, new(h.Vms, new FakeHostPowerGuard(), new()), h.Clock,
            new() { Idle = new() { SchedulerEnabled = false } }, NullLogger<IdleSchedulerService>.Instance);
        await h.Config.SetAsync("memoryPressure", new MemoryPressureConfig(), "admin", default);
        await scheduler.TickAsync(default);
        Assert.Empty(h.Saves);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("incomplete")]
    [InlineData("future")]
    [InlineData("unknown")]
    public async Task Unusable_measurements_do_not_act(string kind)
    {
        var h = new Harness(); await h.Add("vm"); await h.Tick(0);
        h.Clock.UtcNow = Start.AddMinutes(20); h.Sample();
        h.Capacity.Inventory = kind switch
        {
            "stale" => h.Capacity.Inventory with { ObservedAt = Start },
            "future" => h.Capacity.Inventory with { ObservedAt = Start.AddHours(1) },
            "incomplete" => h.Capacity.Inventory with { Complete = false },
            _ => h.Capacity.Inventory with { RamUsedBytes = null }
        };
        await h.Tick(20, measure: false);
        Assert.Empty(h.Saves);
        Assert.Equal("unavailable", h.Status.Status.State);
    }

    [Theory]
    [InlineData(false, "b")]
    [InlineData(true, "a")]
    public async Task Residency_matches_the_backend_and_children_can_be_saved(bool proxmox, string first)
    {
        var h = new Harness(proxmox); await h.Add("a", kind: VmKind.Child, parent: "parent"); await h.Add("b");
        h.Actual[0] = h.Actual[0] with { MemoryAssignedBytes = Gb, MemoryDemandBytes = 10 * Gb };
        await h.Tick(0); await h.Tick(20);
        Assert.Equal(new[] { "save:" + first }, h.Saves);
    }

    [Fact]
    public async Task Save_interval_and_restart_cooldown_are_enforced()
    {
        var h = new Harness(); await h.Add("a"); await h.Add("b");
        await h.Config.SetAsync("memoryPressure", new MemoryPressureConfig(MinSecondsBetweenSaves: 120), "admin", default);
        await h.Tick(0); await h.Tick(20); await h.Tick(21);
        Assert.Single(h.Saves);
        Assert.Equal("cooldown", h.Status.Status.State);
        h.Driver.SetState("a", VmState.Running);
        await h.Tick(22);
        Assert.Equal(new[] { "save:a", "save:b" }, h.Saves);
        Assert.Equal(VmState.Running, h.Driver.StateOf("a"));
    }

    [Fact]
    public async Task Save_failure_is_sanitized_audited_and_not_retried_on_the_same_sample()
    {
        var h = new Harness(); await h.Add("a"); await h.Add("b"); await h.Tick(0);
        h.Driver.PowerFailure = new Exception("secret dependency details");
        await h.Tick(20);
        Assert.Equal("save-failed", h.Status.Status.State);
        var audit = Assert.Single(await h.Audit.QueryAsync(20, default), e => e.Action == "vm.pressure-save");
        Assert.Equal(AuditOutcome.Failure, audit.Outcome);
        Assert.DoesNotContain("secret", audit.Detail);
        await h.Tick(21, measure: false);
        Assert.Single(await h.Audit.QueryAsync(20, default), e => e.Action == "vm.pressure-save");
    }

    [Fact]
    public async Task Maintenance_gate_blocks_the_pressure_phase()
    {
        var h = new Harness(); await h.Add("a"); await h.Tick(0);
        h.Clock.UtcNow = Start.AddMinutes(20); h.Sample();
        var maintenance = new InMemoryMaintenanceGate();
        maintenance.Enter(MaintenanceState.Maintenance, null);
        var scheduler = new IdleSchedulerService(h.Engine, new(h.Vms, new FakeHostPowerGuard(), new()), h.Clock,
            new(), NullLogger<IdleSchedulerService>.Instance, maintenance);
        await scheduler.TickAsync(default);
        Assert.Empty(h.Saves);
        maintenance.Reopen();
        await scheduler.TickAsync(default);
        Assert.Single(h.Saves);
    }

    private sealed class BeforeSaveGate(IVmOperationGate inner, Func<Task> before) : IVmOperationGate
    {
        public async Task<IAsyncDisposable?> TryAcquireAsync(string name, string operation, CancellationToken ct)
        {
            if (operation == "pressure-save") await before();
            return await inner.TryAcquireAsync(name, operation, ct);
        }
        public Task<IAsyncDisposable> AcquireAsync(string name, string operation, CancellationToken ct) => inner.AcquireAsync(name, operation, ct);
        public bool IsHeld(string name, out string? operation) => inner.IsHeld(name, out operation);
    }

    [Fact]
    public async Task Measurement_that_expires_during_planning_cannot_authorize_a_save()
    {
        Harness? h = null;
        h = new(gate: inner => new BeforeSaveGate(inner, () =>
        {
            h!.Clock.UtcNow = h.Clock.UtcNow.AddMinutes(3);
            return Task.CompletedTask;
        }));
        await h.Add("vm"); await h.Tick(0); await h.Tick(20);
        Assert.Empty(h.Saves);
        Assert.Equal("unavailable", h.Status.Status.State);
    }

    [Fact]
    public async Task Activity_arriving_after_planning_is_rechecked()
    {
        Harness? h = null;
        h = new(gate: inner => new BeforeSaveGate(inner, () => h!.Vms.SaveActivityAsync(
            new("vm", true, ["new SSH session"], h.Clock.UtcNow), default)));
        await h.Add("vm"); await h.Tick(0); await h.Tick(20);
        Assert.Empty(h.Saves);
        Assert.Equal("insufficient-candidates", h.Status.Status.State);
    }
}
