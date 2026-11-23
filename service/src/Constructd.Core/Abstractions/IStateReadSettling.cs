namespace Constructd.Core.Abstractions;

/// <summary>
/// A driver whose state reads need no window as long as a real host's cache lag: the fake, which answers at once,
/// so a VM that stays Unknown is given up on in half a second, not fifteen
/// (<see cref="Logic.VmStateReads.GetSettledStateAsync(IHypervisorDriver, string, CancellationToken, TimeSpan?)"/>).
/// </summary>
public interface IStateReadSettling
{
    TimeSpan SettleWindow { get; }

    TimeSpan RereadInterval { get; }
}
