using System.Diagnostics;
using System.Reflection;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Tests.Core;

public sealed class VmStateReadsTests
{
    /// <summary>An <see cref="IHypervisorDriver"/> that only answers state reads, from <see cref="Read"/>.</summary>
    public class StateOnlyDriver : DispatchProxy
    {
        public Func<int, CancellationToken, Task<VmState>> Read { get; set; } = (_, _) => Task.FromResult(VmState.Unknown);
        public int Reads;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IHypervisorDriver.GetStateAsync)
                ? Read(Interlocked.Increment(ref Reads), (CancellationToken)args![1]!)
                : throw new NotSupportedException(targetMethod?.Name);
    }

    private static (IHypervisorDriver Driver, StateOnlyDriver Reads) Driver(Func<int, CancellationToken, Task<VmState>> read)
    {
        var driver = DispatchProxy.Create<IHypervisorDriver, StateOnlyDriver>();
        var reads = (StateOnlyDriver)(object)driver;
        reads.Read = read;
        return (driver, reads);
    }

    [Fact]
    public async Task A_read_that_fails_for_a_moment_is_read_again()
    {
        var (driver, reads) = Driver((n, _) => Task.FromResult(n < 3 ? VmState.Unknown : VmState.Off));
        Assert.Equal(VmState.Off, await driver.GetSettledStateAsync("vm", default, TimeSpan.FromSeconds(10)));
        Assert.Equal(3, reads.Reads);
    }

    [Fact]
    public async Task A_later_read_still_running_at_the_end_of_the_window_is_cut_off()
    {
        var (driver, _) = Driver(async (n, ct) =>
        {
            if (n > 1) await Task.Delay(Timeout.Infinite, ct);
            return VmState.Unknown;
        });
        var clock = Stopwatch.StartNew();
        Assert.Equal(VmState.Unknown, await driver.GetSettledStateAsync("vm", default, TimeSpan.FromSeconds(1)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task The_callers_cancellation_still_throws()
    {
        var (driver, _) = Driver((_, _) => Task.FromResult(VmState.Unknown));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver.GetSettledStateAsync("vm", cancel.Token, TimeSpan.FromSeconds(30)));
    }
}
