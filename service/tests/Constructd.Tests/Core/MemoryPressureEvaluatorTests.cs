using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Tests.Core;

public class MemoryPressureEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private static MemoryPressureVm Vm(string name = "vm", int remaining = 10, long resident = 10) => new(
        new(name, VmState.Running, new(60, IdleAction.Save), Now, Now.AddMinutes(remaining - 60), 0,
            new(name, false, [], Now), TimeSpan.FromMinutes(1), 3), VmKind.Primary, "owner", false, resident);
    private static MemoryPressureInput Input(params MemoryPressureVm[] vms) => new(new(), Now, 95, 100, 0, 100, false, vms);

    [Theory]
    [InlineData(90, false, false)]
    [InlineData(91, false, true)]
    [InlineData(85, false, false)]
    [InlineData(85, true, true)]
    [InlineData(80, true, true)]
    [InlineData(79, true, false)]
    public void High_and_low_watermarks_have_hysteresis(long used, bool previous, bool pressure) =>
        Assert.Equal(pressure, MemoryPressureEvaluator.Plan(Input() with { UsedBytes = used, WasUnderPressure = previous }).UnderPressure);

    [Fact]
    public void Orders_by_remaining_timeout_then_residency_then_name_and_counts_required_saves()
    {
        var plan = MemoryPressureEvaluator.Plan(Input(Vm("z", 20, 50), Vm("b", 5, 10), Vm("a", 5, 10), Vm("small", 5, 5)));
        Assert.Equal(new[] { "a", "b", "small", "z" }, plan.Candidates.Select(c => c.Name));
        Assert.Equal(2, plan.SavesNeeded);
        Assert.False(plan.InsufficientCandidates);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("zero-timeout")]
    [InlineData("connections")]
    [InlineData("busy")]
    [InlineData("job")]
    [InlineData("saved")]
    [InlineData("started")]
    [InlineData("zero-resident")]
    [InlineData("no-report-grace")]
    public void Exclusions_leave_pressure_visible(string exclusion)
    {
        var vm = Vm();
        vm = exclusion switch
        {
            "off" => vm with { Idle = vm.Idle with { Policy = new(60, IdleAction.Off) } },
            "zero-timeout" => vm with { Idle = vm.Idle with { Policy = new(0, IdleAction.Save) } },
            "connections" => vm with { Idle = vm.Idle with { ActiveConnections = 1 } },
            "busy" => vm with { Idle = vm.Idle with { LastReport = new("vm", true, [], Now.AddMinutes(-3)) } },
            "job" => vm with { JobRunning = true },
            "saved" => vm with { LastSavedAt = Now.AddMinutes(-9) },
            "started" => vm with { LastStartedAt = Now.AddMinutes(-9) },
            "zero-resident" => vm with { ResidentBytes = 0 },
            _ => vm with { Idle = vm.Idle with { LastReport = null, LastActiveAt = Now.AddMinutes(-2) } }
        };
        var plan = MemoryPressureEvaluator.Plan(Input(vm));
        Assert.True(plan.UnderPressure);
        Assert.Empty(plan.Candidates);
        Assert.True(plan.InsufficientCandidates);
    }

    [Theory]
    [InlineData(VmState.Off)]
    [InlineData(VmState.Saved)]
    [InlineData(VmState.Paused)]
    [InlineData(VmState.Absent)]
    [InlineData(VmState.Unknown)]
    public void Only_running_vms_are_candidates(VmState state)
    {
        var vm = Vm();
        Assert.Empty(MemoryPressureEvaluator.Plan(Input(vm with { Idle = vm.Idle with { State = state } })).Candidates);
    }

    [Fact]
    public void Stale_busy_report_uses_the_real_idle_timeout_and_children_are_eligible()
    {
        var vm = Vm() with { Kind = VmKind.Child, LastStartedAt = Now.AddMinutes(-10), LastSavedAt = Now.AddMinutes(-10) };
        vm = vm with { Idle = vm.Idle with { LastReport = new("vm", true, [], Now.AddMinutes(-4)) } };
        var candidate = Assert.Single(MemoryPressureEvaluator.Plan(Input(vm)).Candidates);
        Assert.Equal(TimeSpan.FromMinutes(59), candidate.Remaining);
    }

    [Fact]
    public void All_candidates_can_be_insufficient_and_exact_low_water_requires_another_save()
    {
        var plan = MemoryPressureEvaluator.Plan(Input(Vm(resident: 15)));
        Assert.Equal(1, plan.SavesNeeded);
        Assert.True(plan.InsufficientCandidates);
    }

    [Fact]
    public void Swap_can_trigger_below_low_water_but_zero_disables_it()
    {
        var input = Input(Vm()) with { UsedBytes = 60, SwapUsedBytes = 51 };
        var plan = MemoryPressureEvaluator.Plan(input);
        Assert.True(plan.UnderPressure);
        Assert.Equal(1, plan.SavesNeeded);
        Assert.Contains("new measurement", plan.Reason);
        Assert.False(MemoryPressureEvaluator.Plan(input with { Config = new(SwapHighWaterPercent: 0) }).UnderPressure);
        Assert.False(MemoryPressureEvaluator.Plan(input with { SwapTotalBytes = 0 }).UnderPressure);
        Assert.False(MemoryPressureEvaluator.Plan(input with { SwapUsedBytes = 50 }).UnderPressure);
    }

    [Fact]
    public void Disabled_and_unknown_measurements_do_not_act()
    {
        Assert.False(MemoryPressureEvaluator.Plan(Input(Vm()) with { Config = new(Enabled: false) }).UnderPressure);
        Assert.False(MemoryPressureEvaluator.Plan(Input(Vm()) with { TotalBytes = 0 }).UnderPressure);
        Assert.False(MemoryPressureEvaluator.Plan(Input(Vm()) with { UsedBytes = -1 }).UnderPressure);
    }
}
