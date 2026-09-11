namespace Construct.Companion.Core.Abstractions;

// Runs executable + argv without a host shell; stdin is a separate data channel.
// The caller owns started processes and must dispose them to stop and reap them.
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken = default);
    IRunningProcess Start(ProcessInvocation invocation, CancellationToken cancellationToken = default);
}

// Streams text output and reports completion, including startup failure as a fault.
// StopAsync and DisposeAsync stop and reap the child; both must be idempotent.
public interface IRunningProcess : IAsyncDisposable
{
    IAsyncEnumerable<string> StandardOutput { get; }
    IAsyncEnumerable<string> StandardError { get; }
    Task<int> Completion { get; }
    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed record ProcessInvocation(string FileName, IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null, Secret? StandardInput = null, TimeSpan? Timeout = null)
{
    // Lifecycle consoles must stay visible; Electron/Desktop background starts opt in.
    public bool CreateNoWindow { get; init; }
    // Inherit the parent environment, then apply overrides (null removes a key).
    // Values must never enter diagnostics.
    public IReadOnlyDictionary<string, string?>? EnvironmentOverrides { get; init; }
    public override string ToString() => FileName;
}
public sealed record ProcessResult(int Code, string Stdout = "", string Stderr = "")
{
    public override string ToString() => $"ProcessResult {{ Code = {Code} }}";
}
