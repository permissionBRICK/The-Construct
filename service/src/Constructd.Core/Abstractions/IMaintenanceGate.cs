using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

public sealed record DrainResult(bool Drained, IReadOnlyList<(string OperationId, string Kind, string? VmName)> Blocking, TimeSpan Waited);
public interface IMaintenanceGate
{
    MaintenanceState State { get; }
    /// <summary>Atomic admission of a unit of gated work; null ⇒ 503 maintenance. Dispose when the work ends.</summary>
    IDisposable? TryEnter(string kind, string operationId, string? vmName);
    /// <summary>draining; waits until no handle is live or the timeout passes.</summary>
    Task<DrainResult> DrainAsync(TimeSpan timeout, CancellationToken ct);
    void Enter(MaintenanceState state, string? updateId);
    void Reopen();
    int LiveHandles { get; }
}
