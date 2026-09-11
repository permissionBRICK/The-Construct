namespace Construct.Companion.Core.Abstractions;

// Opens a URI/path with ShellExecute or launches a prepared argv invocation.
// Elevated launches use UAC; detached launches outlive the calling Companion.
public interface ILauncher
{
    Task OpenAsync(string target, CancellationToken cancellationToken = default);
    Task LaunchElevatedAsync(ProcessInvocation invocation, CancellationToken cancellationToken = default);
    Task StartDetachedAsync(ProcessInvocation invocation, CancellationToken cancellationToken = default);
}
