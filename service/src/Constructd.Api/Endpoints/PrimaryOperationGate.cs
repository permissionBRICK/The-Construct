using Constructd.Core.Abstractions;

namespace Constructd.Api.Endpoints;

internal static class PrimaryOperationGate
{
    public static async Task<IAsyncDisposable?> AcquireAsync(IVmOperationGate gates, string name, string operation, CancellationToken ct)
    {
        var handle = await gates.TryAcquireAsync(name, operation, ct);
        if (handle is not null) return handle;
        // A background observation must not create a new refusal on the legacy primary API.
        if (gates.IsHeld(name, out var owner) && owner == "capacity-reconcile")
            return await gates.AcquireAsync(name, operation, ct);
        return null;
    }
}
