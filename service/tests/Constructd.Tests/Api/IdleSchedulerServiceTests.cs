using Constructd.Api.Hosting;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Constructd.Tests.Api;

/// <summary>
/// One loop carrying two responsibilities that are switched INDEPENDENTLY: the idle evaluation
/// (§4.7, <c>Idle:SchedulerEnabled</c>) and the host power reconcile (§4.13,
/// <c>Power:KeepHostAwake</c>). Turning one off must not quietly take the other with it.
///
/// Ticks are driven directly, so nothing here waits on a real timer.
/// </summary>
public class IdleSchedulerServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class RecordingIdleEngine : IIdlePolicyEngine
    {
        public int Evaluations { get; private set; }

        public Task<IReadOnlyList<IdleOutcome>> EvaluateAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            Evaluations++;
            return Task.FromResult<IReadOnlyList<IdleOutcome>>([]);
        }
    }

    private sealed class Harness
    {
        public InMemoryVmRepository Vms { get; } = new();

        public RecordingIdleEngine Engine { get; } = new();

        public FakeHostPowerGuard Guard { get; } = new();

        public ConstructdOptions Options { get; } = new();

        public IdleSchedulerService Service { get; }

        public Harness(bool schedulerEnabled, bool keepHostAwake)
        {
            Options.Idle.SchedulerEnabled = schedulerEnabled;
            Options.Power.KeepHostAwake = keepHostAwake;

            Service = new IdleSchedulerService(
                Engine,
                new HostPowerCoordinator(Vms, Guard, Options.Power),
                new MutableClock(Start),
                Options,
                NullLogger<IdleSchedulerService>.Instance);
        }

        public Task AddRunningVmAsync(string name) => Vms.AddAsync(
            new Vm(name, "alice", 4, 8, 64, Start, VmState.Running, null, null, IdlePolicy.Disabled, Vm.NoForwards),
            maxVms: 10,
            CancellationToken.None);
    }

    [Fact]
    public async Task A_tick_does_both_halves_when_both_are_on()
    {
        var harness = new Harness(schedulerEnabled: true, keepHostAwake: true);
        await harness.AddRunningVmAsync("alice-vm");

        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Equal(1, harness.Engine.Evaluations);
        Assert.Equal(1, harness.Guard.AcquireCount);
        Assert.Equal("1 VM(s) running", harness.Guard.Transitions[0].Reason);
    }

    [Fact]
    public async Task Turning_the_idle_scheduler_off_leaves_the_power_request_working()
    {
        var harness = new Harness(schedulerEnabled: false, keepHostAwake: true);
        await harness.AddRunningVmAsync("alice-vm");

        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Equal(0, harness.Engine.Evaluations);
        Assert.Equal(1, harness.Guard.AcquireCount);
    }

    [Fact]
    public async Task Turning_the_power_request_off_leaves_the_idle_evaluation_working()
    {
        var harness = new Harness(schedulerEnabled: true, keepHostAwake: false);
        await harness.AddRunningVmAsync("alice-vm");

        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Equal(1, harness.Engine.Evaluations);
        Assert.Empty(harness.Guard.Transitions);
    }

    [Fact]
    public async Task The_power_request_is_taken_before_the_first_tick()
    {
        // A service restart while VMs are running must take the request back immediately, not a
        // minute later. This is the one case that has to go through the real hosted-service start.
        var harness = new Harness(schedulerEnabled: false, keepHostAwake: true);
        harness.Options.Idle.TickSeconds = 3600;        // no tick can fire while this test runs
        await harness.AddRunningVmAsync("alice-vm");

        await harness.Service.StartAsync(CancellationToken.None);
        try
        {
            // StartAsync returns as soon as ExecuteAsync yields, which may be before the startup
            // reconcile has landed.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && harness.Guard.AcquireCount == 0)
            {
                await Task.Delay(10);
            }
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, harness.Guard.AcquireCount);
        Assert.Equal(0, harness.Engine.Evaluations);
    }

    // ── The composition root's half of the same decision ─────────────────────

    private static bool SchedulerIsRegistered(bool schedulerEnabled, bool keepHostAwake)
    {
        using var app = new TestApp(new Dictionary<string, string?>
        {
            ["Constructd:Idle:SchedulerEnabled"] = schedulerEnabled ? "true" : "false",
            ["Constructd:Power:KeepHostAwake"] = keepHostAwake ? "true" : "false",
            // A tick would otherwise fire inside the test; the loop's presence is what is asserted.
            ["Constructd:Idle:TickSeconds"] = "3600",
        });

        return app.Services.GetServices<IHostedService>().OfType<IdleSchedulerService>().Any();
    }

    [Fact]
    public void The_loop_runs_when_only_the_host_power_request_is_wanted()
    {
        Assert.True(SchedulerIsRegistered(schedulerEnabled: false, keepHostAwake: true));
    }

    [Fact]
    public void The_loop_runs_when_only_the_idle_evaluation_is_wanted()
    {
        Assert.True(SchedulerIsRegistered(schedulerEnabled: true, keepHostAwake: false));
    }

    [Fact]
    public void The_loop_does_not_run_when_neither_is_wanted()
    {
        Assert.False(SchedulerIsRegistered(schedulerEnabled: false, keepHostAwake: false));
    }
}
