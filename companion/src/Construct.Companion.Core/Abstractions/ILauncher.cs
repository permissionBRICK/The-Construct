namespace Construct.Companion.Core.Abstractions;

// Opens a URI/path with ShellExecute or launches a prepared argv invocation that
// outlives the Companion. Elevation is carried by the invocation itself (Start-Process
// -Verb RunAs inside the encoded command); LaunchElevatedAsync only refuses anything else.
public interface ILauncher
{
    Task OpenAsync(string target, CancellationToken cancellationToken = default);
    Task LaunchElevatedAsync(ProcessInvocation invocation, CancellationToken cancellationToken = default);
    Task StartDetachedAsync(ProcessInvocation invocation, CancellationToken cancellationToken = default);
}

// The platform half of DesktopLauncher. Unlike IProcessRunner, these invocations intentionally outlive the caller.
public interface IDesktopProcess
{
    Task OpenAsync(string target, CancellationToken cancellationToken = default);
    Task StartAsync(ProcessInvocation invocation, CancellationToken cancellationToken = default);
    string? FindOnPath(string executable);
    string? EnvironmentValue(string name);
}
