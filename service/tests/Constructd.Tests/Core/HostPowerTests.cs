using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;
using Constructd.Fakes;

namespace Constructd.Tests.Core;

/// <summary>
/// "The host must not sleep under running VMs" (plan §4.13): the pure rule, the reconciler that
/// applies it to the VM registry, and the guard state machine the Windows P/Invoke sits on.
/// </summary>
public class HostPowerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static Vm VmIn(string name, VmState state) =>
        new(name, "alice", 4, 8, 64, Start, state, null, null, IdlePolicy.Disabled, Vm.NoForwards);

    // ── The pure rule ────────────────────────────────────────────────────────

    [Fact]
    public void No_vms_means_no_request()
    {
        var plan = HostPowerPlanner.Plan([]);

        Assert.False(plan.Required);
        Assert.Equal("no VM is running", plan.Reason);
    }

    [Fact]
    public void One_running_vm_requires_the_host()
    {
        var plan = HostPowerPlanner.Plan([VmState.Running]);

        Assert.True(plan.Required);
        Assert.Equal("1 VM(s) running", plan.Reason);
    }

    [Fact]
    public void Only_running_vms_count()
    {
        var plan = HostPowerPlanner.Plan(
            [VmState.Running, VmState.Saved, VmState.Off, VmState.Paused, VmState.Absent, VmState.Unknown, VmState.Running]);

        Assert.True(plan.Required);
        Assert.Equal("2 VM(s) running", plan.Reason);
    }

    [Fact]
    public void A_host_whose_vms_are_all_saved_or_off_may_sleep()
    {
        Assert.False(HostPowerPlanner.Plan([VmState.Saved, VmState.Off, VmState.Absent]).Required);
    }

    // ── The reconciler over the registry ─────────────────────────────────────

    private sealed class Harness
    {
        public InMemoryVmRepository Vms { get; } = new();

        public FakeHostPowerGuard Guard { get; } = new();

        public PowerOptions Options { get; } = new();

        public HostPowerCoordinator Coordinator { get; }

        public Harness() => Coordinator = new HostPowerCoordinator(Vms, Guard, Options);

        public Task AddAsync(string name, VmState state) =>
            Vms.AddAsync(VmIn(name, state), maxVms: 10, CancellationToken.None);

        public Task SetStateAsync(string name, VmState state) =>
            Vms.UpdateAsync(VmIn(name, state), CancellationToken.None);

        public Task<HostPowerRequest?> ReconcileAsync() => Coordinator.ReconcileAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_guard_follows_the_vms_starting_and_stopping()
    {
        var harness = new Harness();

        await harness.ReconcileAsync();
        Assert.Equal(0, harness.Guard.AcquireCount);

        await harness.AddAsync("alice-vm", VmState.Running);
        await harness.ReconcileAsync();
        Assert.True(harness.Guard.IsRequired);
        Assert.Equal(1, harness.Guard.AcquireCount);
        Assert.Equal("1 VM(s) running", harness.Guard.Transitions[^1].Reason);

        // A second VM starts: still one request, and no second platform call.
        await harness.AddAsync("alice-vm2", VmState.Running);
        await harness.ReconcileAsync();
        Assert.Equal(1, harness.Guard.AcquireCount);

        await harness.SetStateAsync("alice-vm", VmState.Saved);
        await harness.ReconcileAsync();
        Assert.True(harness.Guard.IsRequired);
        Assert.Equal(0, harness.Guard.ReleaseCount);

        await harness.SetStateAsync("alice-vm2", VmState.Off);
        await harness.ReconcileAsync();
        Assert.False(harness.Guard.IsRequired);
        Assert.Equal(1, harness.Guard.ReleaseCount);
        Assert.Equal("no VM is running", harness.Guard.Transitions[^1].Reason);

        // And back again — the request can be taken a second time.
        await harness.SetStateAsync("alice-vm", VmState.Running);
        await harness.ReconcileAsync();
        Assert.Equal(2, harness.Guard.AcquireCount);
    }

    [Fact]
    public async Task Reconciling_the_same_state_again_touches_nothing()
    {
        var harness = new Harness();
        await harness.AddAsync("alice-vm", VmState.Running);

        for (var tick = 0; tick < 5; tick++)
        {
            await harness.ReconcileAsync();
        }

        Assert.Single(harness.Guard.Transitions);
    }

    [Fact]
    public async Task A_deleted_vm_releases_the_request()
    {
        var harness = new Harness();
        await harness.AddAsync("alice-vm", VmState.Running);
        await harness.ReconcileAsync();

        await harness.Vms.RemoveAsync("alice-vm", CancellationToken.None);
        await harness.ReconcileAsync();

        Assert.False(harness.Guard.IsRequired);
        Assert.Equal(1, harness.Guard.ReleaseCount);
    }

    [Fact]
    public async Task With_KeepHostAwake_off_no_request_is_ever_taken()
    {
        var harness = new Harness();
        harness.Options.KeepHostAwake = false;
        await harness.AddAsync("alice-vm", VmState.Running);

        Assert.Null(await harness.ReconcileAsync());
        Assert.Empty(harness.Guard.Transitions);
        Assert.False(harness.Guard.IsRequired);
    }

    [Fact]
    public void KeepHostAwake_is_on_by_default()
    {
        Assert.True(new ConstructdOptions().Power.KeepHostAwake);
    }

    // ── The guard state machine ──────────────────────────────────────────────

    [Fact]
    public void Dispose_releases_a_held_request()
    {
        var guard = new FakeHostPowerGuard();
        guard.SetRequired(true, "1 VM(s) running");

        guard.Dispose();

        Assert.False(guard.IsRequired);
        Assert.Equal(1, guard.ReleaseCount);
        Assert.Equal("the service is stopping", guard.Transitions[^1].Reason);
        Assert.True(guard.DisposedCore);
    }

    [Fact]
    public void Dispose_without_a_request_releases_nothing()
    {
        var guard = new FakeHostPowerGuard();

        guard.Dispose();
        guard.Dispose();

        Assert.Empty(guard.Transitions);
        Assert.True(guard.DisposedCore);
    }

    [Fact]
    public void Nothing_is_acquired_after_dispose()
    {
        var guard = new FakeHostPowerGuard();
        guard.Dispose();

        guard.SetRequired(true, "1 VM(s) running");

        Assert.Empty(guard.Transitions);
        Assert.False(guard.IsRequired);
    }

    [Fact]
    public void Dispose_closes_the_platform_handle_even_when_the_release_fails()
    {
        var guard = new ThrowingGuard { Fail = false };
        guard.SetRequired(true, "1 VM(s) running");
        guard.FailRelease = true;

        // The failure is reported rather than swallowed...
        Assert.Throws<InvalidOperationException>(() => guard.Dispose());

        // ...and the handle is closed all the same. This method never runs again.
        Assert.True(guard.DisposedCore);
    }

    [Fact]
    public void A_failed_acquire_is_not_remembered_as_held()
    {
        var guard = new ThrowingGuard();

        Assert.Throws<InvalidOperationException>(() => guard.SetRequired(true, "1 VM(s) running"));
        Assert.False(guard.IsRequired);

        // The next tick tries again rather than assuming the request is held.
        guard.Fail = false;
        guard.SetRequired(true, "1 VM(s) running");
        Assert.True(guard.IsRequired);
        Assert.Equal(2, guard.Attempts);
    }

    [Fact]
    public async Task Concurrent_callers_never_double_acquire()
    {
        var guard = new FakeHostPowerGuard();

        await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() =>
            guard.SetRequired(i % 2 == 0, "concurrent"))));

        // Whatever the interleaving, acquire and release strictly alternate.
        var transitions = guard.Transitions;
        for (var i = 1; i < transitions.Count; i++)
        {
            Assert.NotEqual(transitions[i - 1].Required, transitions[i].Required);
        }

        Assert.True(Math.Abs(guard.AcquireCount - guard.ReleaseCount) <= 1);
    }

    private sealed class ThrowingGuard : HostPowerGuardBase
    {
        public bool Fail { get; set; } = true;

        public bool FailRelease { get; set; }

        public int Attempts { get; private set; }

        public bool DisposedCore { get; private set; }

        protected override void Acquire(string reason)
        {
            Attempts++;
            if (Fail)
            {
                throw new InvalidOperationException("PowerSetRequest failed.");
            }
        }

        protected override void Release(string reason)
        {
            if (FailRelease)
            {
                throw new InvalidOperationException("PowerClearRequest failed.");
            }
        }

        protected override void DisposeCore() => DisposedCore = true;
    }
}
