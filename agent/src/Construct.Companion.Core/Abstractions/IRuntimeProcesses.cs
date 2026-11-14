namespace Construct.Companion.Core.Abstractions;

public sealed record SupervisedProcessState(string State, int Failures = 0, string Message = "");
public sealed record ProcessSupervisionOptions(TimeSpan Settle, TimeSpan? HeartbeatTimeout = null, bool Restart = true);
public interface ISupervisedProcess : IAsyncDisposable
{
    SupervisedProcessState State { get; }
    Task<bool> FirstAttempt { get; }
    Task Completion { get; }
}
public interface IRuntimeProcesses
{
    ISupervisedProcess Start(Func<CancellationToken, IRunningProcess> spawn, ProcessSupervisionOptions options,
        Action<SupervisedProcessState> changed, Func<string, CancellationToken, Task>? output = null);
}
// A process-wide reservation covers the probe-to-bind gap between instance runtimes.
public interface IPortReservations
{
    IDisposable? TryReserve(int port);
}
