using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Logic;

namespace Constructd.Api.Hosting;

/// <summary>One read-only all-VM sample per ten seconds while clients request inventory.
/// Kept separate from admission/reconciliation: looking at usage never changes reservations.</summary>
public sealed class VmResourceUsageReader(IHypervisorInventory inventory, IClock clock) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InventorySnapshot? _sample;
    private DateTimeOffset? _readAt;
    private bool _failed;

    public async Task<VmResourceUsageResponse> ReadAsync(string name, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_readAt is null || clock.UtcNow - _readAt >= TimeSpan.FromSeconds(10))
            {
                try
                {
                    var next = await inventory.ReadAsync(ct);
                    _failed = !next.Complete && next.Vms.Count == 0;
                    if (!_failed) _sample = next;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { _failed = true; } // No dependency exception text in an API response.
                _readAt = clock.UtcNow;
            }
            var actual = _sample?.Vms.FirstOrDefault(v => Ownership.SameName(v.Name, name));
            if (actual is null) return new(null, true, null, null, null, null, null, null);
            var stale = _failed || clock.UtcNow - _sample!.ObservedAt > TimeSpan.FromSeconds(30);
            // Memory demand may be absent/zero when integration services cannot report it.
            var demand = actual.MemoryDemandBytes is > 0 ? actual.MemoryDemandBytes
                : actual.State is Constructd.Core.Domain.VmState.Off or Constructd.Core.Domain.VmState.Saved ? 0L : (long?)null;
            return new(_sample!.ObservedAt, stale, actual.State,
                actual.CpuUsagePercent is >= 0 and <= 100 ? actual.CpuUsagePercent : null,
                actual.MemoryAssignedBytes, demand,
                actual.Complete && actual.Disks.All(d => d.Readable)
                    ? actual.Disks.DistinctBy(d => d.Path, StringComparer.OrdinalIgnoreCase).Sum(d => d.FileBytes) : null,
                actual.UptimeSeconds is >= 0 ? actual.UptimeSeconds : null);
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
