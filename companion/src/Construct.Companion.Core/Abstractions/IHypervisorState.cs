namespace Construct.Companion.Core.Abstractions;

// Reads local hypervisor state without changing VM power or configuration.
// Implementations map native states; callers decide which actions are available.
public interface IHypervisorState
{
    Task<HypervisorState> QueryAsync(string vmName, CancellationToken cancellationToken = default);
}
public enum HypervisorState { Running, Off, Saved, Paused, Absent, Unknown }
