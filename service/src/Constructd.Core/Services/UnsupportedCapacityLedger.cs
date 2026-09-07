using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Core.Services;

public sealed class UnsupportedCapacityLedger : ICapacityLedger
{
    public Task<HostCapacitySnapshot> SnapshotAsync(bool refresh, CancellationToken ct) => Task.FromResult(new HostCapacitySnapshot(0, DateTimeOffset.MinValue, false, 0, 0, 0, 0, 0, 0, 0, null, 0, null, [], [], []));
    public Task<CapacityDecision> TryReserveAsync(ReservationRequest request, CancellationToken ct) => Task.FromResult(new CapacityDecision(false, [], null, "host", 0, 0, 0, "unsupported-capability", 0));
    public Task ConfirmAsync(IReadOnlyList<string> ids, VmState observed, CancellationToken ct) => throw Unsupported();
    public Task ReleaseAsync(IReadOnlyList<string> ids, VmState observed, string reason, CancellationToken ct) => throw Unsupported();
    public Task TrimAsync(string id, long amount, CancellationToken ct) => throw Unsupported();
    public Task ExtendAsync(IReadOnlyList<string> ids, TimeSpan by, CancellationToken ct) => throw Unsupported();
    public Task<IReadOnlyList<OrphanOutcome>> ReconcileAsync(CancellationToken ct) => throw Unsupported();
    private static NotSupportedException Unsupported() => new("Capacity backend is unsupported.");
}
