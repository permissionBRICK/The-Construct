using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Fakes;

public sealed class FakeProcessRunner : IProcessRunner
{
    public Func<ProcessInvocation, ProcessResult>? Handler { get; set; }
    public List<ProcessInvocation> Invocations { get; } = [];
    public Queue<ProcessResult> Results { get; } = new();
    public List<FakeRunningProcess> Processes { get; } = [];
    public Task<ProcessResult> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Invocations.Add(invocation);
        return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : Handler?.Invoke(invocation) ?? new ProcessResult(0));
    }
    public IRunningProcess Start(ProcessInvocation invocation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Invocations.Add(invocation);
        var process = new FakeRunningProcess(cancellationToken); Processes.Add(process); return process;
    }
}
