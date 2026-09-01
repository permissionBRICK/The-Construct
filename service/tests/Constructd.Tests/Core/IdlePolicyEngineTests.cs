using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;
using Constructd.Fakes;

namespace Constructd.Tests.Core;

/// <summary>
/// The engine around the pure evaluator: it gathers the signals, applies the decision through the
/// driver and audits it.
/// </summary>
public class IdlePolicyEngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public MutableClock Clock { get; } = new(Start);

        public InMemoryVmRepository Vms { get; } = new();

        public FakeHypervisorDriver Driver { get; } = new();

        public InMemoryAuditLog Audit { get; } = new();

        public InMemoryPortForwardManager Forwards { get; }

        public IdleOptions Options { get; } = new()
        {
            ReportIntervalMinutes = 5,
            MissingReportGraceMultiple = 3,
        };

        public IdlePolicyEngine Engine { get; }

        public Harness()
        {
            Forwards = new InMemoryPortForwardManager(
                Clock,
                Vms,
                new InMemoryForwardStore(),
                new PortRangeOptions(2201, 2299),
                new PortRangeOptions(2300, 2999));

            Engine = new IdlePolicyEngine(Vms, Forwards, Driver, Audit, Options);
        }

        public async Task<Vm> AddRunningVmAsync(string name, IdlePolicy policy)
        {
            var vm = new Vm(name, "owner", 4, 8, 64, Start, VmState.Running, 2201, null, policy, Vm.NoForwards);
            await Vms.AddAsync(vm, maxVms: 10, CancellationToken.None);
            Driver.SetState(name, VmState.Running);
            return vm;
        }
    }

    [Fact]
    public async Task Saves_a_vm_that_has_been_idle_for_the_whole_window()
    {
        var harness = new Harness();
        await harness.AddRunningVmAsync("work-vm", new IdlePolicy(60, IdleAction.Save));

        // First tick starts the idle window, the second one is two hours later.
        await harness.Engine.EvaluateAsync(Start, CancellationToken.None);
        var outcomes = await harness.Engine.EvaluateAsync(Start.AddHours(2), CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(IdleDecisionKind.Save, outcome.Decision.Kind);
        Assert.True(outcome.Applied);
        Assert.Equal(VmState.Saved, harness.Driver.StateOf("work-vm"));

        var vm = await harness.Vms.GetAsync("work-vm", CancellationToken.None);
        Assert.Equal(VmState.Saved, vm!.State);

        var audit = await harness.Audit.QueryAsync(10, CancellationToken.None);
        Assert.Contains(audit, entry => entry is { Action: "vm.idle-save", Actor: "system", Outcome: AuditOutcome.Success });
    }

    [Fact]
    public async Task A_busy_heartbeat_keeps_the_vm_running_with_zero_connections()
    {
        var harness = new Harness();
        await harness.AddRunningVmAsync("work-vm", new IdlePolicy(60, IdleAction.Save));

        await harness.Engine.EvaluateAsync(Start, CancellationToken.None);

        var later = Start.AddHours(2);
        await harness.Vms.SaveActivityAsync(
            new ActivityReport("work-vm", true, ["codex working"], later.AddMinutes(-1)),
            CancellationToken.None);

        var outcomes = await harness.Engine.EvaluateAsync(later, CancellationToken.None);

        Assert.Equal(IdleDecisionKind.KeepAlive, outcomes[0].Decision.Kind);
        Assert.Equal(VmState.Running, harness.Driver.StateOf("work-vm"));
    }

    [Fact]
    public async Task Live_connections_keep_the_vm_running()
    {
        var harness = new Harness();
        await harness.AddRunningVmAsync("work-vm", new IdlePolicy(60, IdleAction.Save));
        harness.Forwards.SetActiveConnections("work-vm", 1);

        await harness.Engine.EvaluateAsync(Start, CancellationToken.None);
        var outcomes = await harness.Engine.EvaluateAsync(Start.AddHours(5), CancellationToken.None);

        Assert.Equal(IdleDecisionKind.KeepAlive, outcomes[0].Decision.Kind);
        Assert.Equal(VmState.Running, harness.Driver.StateOf("work-vm"));
    }

    [Fact]
    public async Task Shutdown_policy_stops_the_vm()
    {
        var harness = new Harness();
        await harness.AddRunningVmAsync("work-vm", new IdlePolicy(30, IdleAction.Shutdown));

        await harness.Engine.EvaluateAsync(Start, CancellationToken.None);
        var outcomes = await harness.Engine.EvaluateAsync(Start.AddHours(1), CancellationToken.None);

        Assert.Equal(IdleDecisionKind.Shutdown, outcomes[0].Decision.Kind);
        Assert.Equal(VmState.Off, harness.Driver.StateOf("work-vm"));
    }

    [Fact]
    public async Task A_disabled_policy_never_acts()
    {
        var harness = new Harness();
        await harness.AddRunningVmAsync("work-vm", IdlePolicy.Disabled);

        await harness.Engine.EvaluateAsync(Start, CancellationToken.None);
        var outcomes = await harness.Engine.EvaluateAsync(Start.AddDays(7), CancellationToken.None);

        Assert.Equal(IdleDecisionKind.None, outcomes[0].Decision.Kind);
        Assert.False(outcomes[0].Applied);
        Assert.Equal(VmState.Running, harness.Driver.StateOf("work-vm"));
    }

    [Fact]
    public async Task The_admin_cap_applies_to_policies_stored_before_it_was_lowered()
    {
        var harness = new Harness();
        harness.Options.MaxTimeoutMinutes = 30;
        await harness.AddRunningVmAsync("work-vm", new IdlePolicy(600, IdleAction.Save));

        await harness.Engine.EvaluateAsync(Start, CancellationToken.None);
        var outcomes = await harness.Engine.EvaluateAsync(Start.AddMinutes(45), CancellationToken.None);

        Assert.Equal(IdleDecisionKind.Save, outcomes[0].Decision.Kind);
    }

    [Fact]
    public async Task The_engine_refreshes_a_stale_state_from_the_driver()
    {
        var harness = new Harness();
        await harness.AddRunningVmAsync("work-vm", new IdlePolicy(60, IdleAction.Save));
        harness.Driver.SetState("work-vm", VmState.Off);

        await harness.Engine.EvaluateAsync(Start, CancellationToken.None);

        var vm = await harness.Vms.GetAsync("work-vm", CancellationToken.None);
        Assert.Equal(VmState.Off, vm!.State);
    }
}
