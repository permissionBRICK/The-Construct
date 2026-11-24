using Constructd.Core.Abstractions;

namespace Constructd.Api.Endpoints;

internal static class PrimaryOperationGate
{
    public static async Task<IAsyncDisposable?> AcquireAsync(IVmOperationGate gates, string name, string operation, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var handle = await gates.TryAcquireAsync(name, operation, ct);
            if (handle is not null) return handle;
            // Released between TryAcquireAsync and IsHeld: retry the non-blocking acquire.
            if (!gates.IsHeld(name, out var owner)) continue;
            // A background observation or startup reconcile must not refuse a primary VM operation.
            if (owner is "capacity-reconcile" or "idle-evaluate" or "pressure-evaluate" or "lease-reconcile"
                or Constructd.Api.Jobs.HostStartSettings.GateOwner or Constructd.Api.Jobs.PrimaryMemoryFloor.GateOwner)
                return await gates.AcquireAsync(name, operation, ct);
            return null;
        }

        return await gates.TryAcquireAsync(name, operation, ct);
    }
}
