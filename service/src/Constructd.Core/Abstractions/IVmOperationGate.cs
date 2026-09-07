using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

public interface IVmOperationGate
{
    /// <summary>Waits for the VM's gate. Lifecycle, delete, hardware, media, reconcile-per-VM, expiry all take it.</summary>
    Task<IAsyncDisposable> AcquireAsync(string vmName, string operationId, CancellationToken ct);
    /// <summary>Non-blocking variant for reconciliation: null when an operation holds the gate.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string vmName, string operationId, CancellationToken ct);
    bool IsHeld(string vmName, out string? operationId);
}
