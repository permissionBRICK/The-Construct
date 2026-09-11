using Construct.Companion.Core.Abstractions;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Runtime;
namespace Construct.Companion.Tests.Runtime;

public sealed class ProcessSupervisorTests
{
    internal static async Task Eventually(Func<bool> condition)
    { for (var i = 0; i < 1000 && !condition(); i++) await Task.Delay(2); Assert.True(condition(), "Condition did not become true."); }
    [Fact]
    public async Task WatcherDeathBacksOffAndDisposeCancelsPendingRestart()
    {
        var clock = new FakeClock(); var children = new List<FakeRunningProcess>();
        var supervisor = new SshProcessSupervisor(clock);
        await using var process = supervisor.Start(ct => { var p = new FakeRunningProcess(ct); children.Add(p); return p; }, new(TimeSpan.Zero, TimeSpan.FromSeconds(90)), _ => { });
        Assert.True(await process.FirstAttempt); children[0].Exit(255);
        await Eventually(() => process.State.Failures == 1 && clock.PendingDelays == 1);
        clock.Advance(TimeSpan.FromMilliseconds(1999)); Assert.Single(children);
        clock.Advance(TimeSpan.FromMilliseconds(1)); await Eventually(() => children.Count == 2 && process.State.State == "up");
        children[1].Exit(255); await Eventually(() => process.State.Failures == 2 && clock.PendingDelays == 1);
        await process.DisposeAsync(); clock.Advance(TimeSpan.FromMinutes(5)); Assert.Equal(2, children.Count);
        Assert.All(children, child => Assert.True(child.Stopped)); Assert.Equal(0, clock.PendingDelays);
    }
    [Fact]
    public async Task HeartbeatsExtendDeadlineAndSilenceKillsWatcher()
    {
        var clock = new FakeClock(); FakeRunningProcess? child = null;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var process = new SshProcessSupervisor(clock).Start(ct => child = new(ct), new(TimeSpan.Zero, TimeSpan.FromSeconds(90)), _ => { }, (_, _) => { received.TrySetResult(); return Task.CompletedTask; });
        Assert.True(await process.FirstAttempt); clock.Advance(TimeSpan.FromSeconds(60)); child!.Emit("#\n");
        await received.Task.WaitAsync(TimeSpan.FromSeconds(3)); clock.Advance(TimeSpan.FromSeconds(30));
        await Eventually(() => clock.PendingDelays == 1); Assert.False(child.Stopped);
        clock.Advance(TimeSpan.FromSeconds(60)); await Eventually(() => child.Stopped && process.State.Failures == 1);
    }
    [Fact]
    public async Task EarlyFailureIsReportedAndHealthyConnectionResetsBackoff()
    {
        var clock = new FakeClock(); var children = new List<FakeRunningProcess>();
        await using var process = new SshProcessSupervisor(clock).Start(ct => { var p = new FakeRunningProcess(ct); children.Add(p); return p; }, new(TimeSpan.FromMilliseconds(1200)), _ => { });
        children[0].Exit(255); Assert.False(await process.FirstAttempt);
        await Eventually(() => clock.PendingDelays == 1 && process.State.Failures == 1); Assert.Equal("failed", process.State.State);
        clock.Advance(TimeSpan.FromSeconds(2)); await Eventually(() => children.Count == 2 && clock.PendingDelays >= 1);
        clock.Advance(TimeSpan.FromMilliseconds(1200)); await Eventually(() => process.State.State == "up");
        clock.Advance(TimeSpan.FromSeconds(60)); children[1].Exit(); await Eventually(() => process.State.Failures == 1 && process.State.State == "starting");
    }
    [Fact]
    public async Task FiveFailedRestartsReportPersistentFailure()
    {
        var clock = new FakeClock(); var children = new List<FakeRunningProcess>();
        await using var process = new SshProcessSupervisor(clock).Start(ct => { var p = new FakeRunningProcess(ct); children.Add(p); return p; }, new(TimeSpan.Zero), _ => { });
        Assert.True(await process.FirstAttempt);
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            children[^1].Exit(); await Eventually(() => process.State.Failures == attempt && clock.PendingDelays == 1);
            if (attempt < 6) { clock.Advance(TimeSpan.FromMilliseconds(2000 * Math.Pow(2, attempt - 1))); await Eventually(() => children.Count == attempt + 1 && process.State.State == "up"); }
        }
        Assert.Equal("failed", process.State.State); Assert.Equal(6, process.State.Failures);
    }
}
