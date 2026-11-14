using Constructd.Core.Abstractions;
using Constructd.Core.Services;

namespace Constructd.Api.Repair;

/// <summary>
/// The VM operation gate with the repair rule (design 11.5): whoever takes a VM's gate for a job or a
/// change ends the VM's repair session first, while holding the gate, so a repair and a lifecycle job never
/// run on one VM at once. A repair takes the inner gate itself while it starts. Background passes that only
/// look at a VM (reconciles, idle and pressure checks, lease ticks) leave the repair running.
/// </summary>
public sealed class RepairAwareVmOperationGate(InMemoryVmOperationGate inner, RepairSessions repairs) : IVmOperationGate
{
    private static readonly HashSet<string> Passes = new(StringComparer.Ordinal)
    {
        "capacity-reconcile", "idle-evaluate", "pressure-evaluate", "lease-reconcile", "lease-tick", "memory-floor", "https-start-on-request",
    };

    public async Task<IAsyncDisposable> AcquireAsync(string vmName, string operationId, CancellationToken ct)
    {
        var handle = await inner.AcquireAsync(vmName, operationId, ct);
        return await EndRepairAsync(vmName, operationId, handle);
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(string vmName, string operationId, CancellationToken ct)
    {
        var handle = await inner.TryAcquireAsync(vmName, operationId, ct);
        return handle is null ? null : await EndRepairAsync(vmName, operationId, handle);
    }

    public bool IsHeld(string vmName, out string? operationId) => inner.IsHeld(vmName, out operationId);

    private async Task<IAsyncDisposable> EndRepairAsync(string vmName, string operationId, IAsyncDisposable handle)
    {
        if (Passes.Contains(operationId) || operationId.StartsWith("repair:", StringComparison.Ordinal)) return handle;
        try { await repairs.EndForJobAsync(vmName); }
        catch
        {
            await handle.DisposeAsync();
            throw;
        }

        return handle;
    }
}
