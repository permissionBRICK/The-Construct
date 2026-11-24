using Constructd.Core.Abstractions;

namespace Constructd.Api.Endpoints;

internal static class PrimaryOperationGate
{
    private static readonly TimeSpan BackgroundWaitLimit = TimeSpan.FromSeconds(30);

    private static bool IsBackgroundOwner(string? owner) =>
        owner is "capacity-reconcile" or "idle-evaluate" or "pressure-evaluate" or "lease-reconcile"
            or Constructd.Api.Jobs.HostStartSettings.GateOwner
            or Constructd.Api.Jobs.PrimaryMemoryFloor.GateOwner;

    public static async Task<IAsyncDisposable?> AcquireAsync(IVmOperationGate gates, string name, string operation, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var handle = await gates.TryAcquireAsync(name, operation, ct);
            if (handle is not null) return handle;
            // Released between TryAcquireAsync and IsHeld: retry the non-blocking acquire briefly.
            if (!gates.IsHeld(name, out var owner)) continue;
            // A background observation or startup reconcile must not refuse a primary VM operation,
            // but bound the wait so a real job slipping in afterwards cannot block indefinitely.
            if (IsBackgroundOwner(owner))
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(BackgroundWaitLimit);
                try { return await gates.AcquireAsync(name, operation, wait.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
            }

            return null;
        }

        return await gates.TryAcquireAsync(name, operation, ct);
    }
}
