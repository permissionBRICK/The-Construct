using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Fakes;

public sealed class FakeLauncher : ILauncher
{
    public List<string> Opened { get; } = [];
    public List<ProcessInvocation> Elevated { get; } = [];
    public List<ProcessInvocation> Detached { get; } = [];
    public Task OpenAsync(string target, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Opened.Add(target); return Task.CompletedTask; }
    public Task LaunchElevatedAsync(ProcessInvocation invocation, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Elevated.Add(invocation); return Task.CompletedTask; }
    public Task StartDetachedAsync(ProcessInvocation invocation, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Detached.Add(invocation); return Task.CompletedTask; }
}
