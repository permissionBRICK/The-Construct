using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

/// <summary>What the host's power availability request should be right now, and why.</summary>
/// <param name="Required">True while the host must not sleep.</param>
/// <param name="Reason">Log-safe explanation, e.g. <c>"2 VM(s) running"</c>.</param>
public readonly record struct HostPowerRequest(bool Required, string Reason);

/// <summary>
/// THE rule, and it is pure: the host must stay awake exactly while at least one service-managed VM
/// is <see cref="VmState.Running"/>. Saved, off, paused, absent and unknown VMs need nothing — a
/// saved VM is state on disk, and resuming it is a user action that wakes the host anyway.
/// </summary>
public static class HostPowerPlanner
{
    public static HostPowerRequest Plan(IEnumerable<VmState> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        var running = states.Count(state => state == VmState.Running);

        return running > 0
            ? new HostPowerRequest(true, $"{running} VM(s) running")
            : new HostPowerRequest(false, "no VM is running");
    }
}
