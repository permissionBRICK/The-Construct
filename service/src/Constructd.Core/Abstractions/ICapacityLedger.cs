using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

public sealed record ReservationLine(ReservationResource Resource, long Amount, string? Artifact, string? Volume);
public sealed record ReservationRequest(string Owner, string? VmName, string OperationId, IReadOnlyList<ReservationLine> Lines, TimeSpan PendingTimeout);
/// <param name="AllowedAmount">The limit that applied (wire: <c>allowed</c>).</param>
public sealed record CapacityDecision(
    bool Allowed,
    IReadOnlyList<string> ReservationIds,
    string? Resource,
    string? Scope,
    long Requested,
    long AllowedAmount,
    long Available,
    string? Reason,
    long Epoch);
public sealed record VolumeCapacity(string Root, long TotalBytes, long FreeBytes, long HeadroomBytes, long GrowthReservedBytes, long AvailableBytes);
public sealed record HostCapacitySnapshot(
    long Epoch,
    DateTimeOffset ObservedAt,
    bool Complete,
    long RamTotalBytes,
    long RamHeadroomBytes,
    long RamReservedBytes,
    long RamUnmanagedBytes,
    long RamPhysicalFreeBytes,
    long RamAvailableBytes,
    int CpuLogical,
    int? CpuBudget,
    int CpuActive,
    int? CpuAvailable,
    IReadOnlyList<VolumeCapacity> Volumes,
    IReadOnlyList<Reservation> Reservations,
    IReadOnlyList<HypervisorVmInfo> Unmanaged,
    IReadOnlyList<string>? Problems = null,
    IReadOnlyDictionary<string, long>? RamAvailableByVm = null,
    IReadOnlyList<Reservation>? Accounting = null);

public interface ICapacityLedger
{
    /// <summary>Serialized (one gate) + one IMMEDIATE transaction; pending rows carry OperationId and PendingUntil.</summary>
    Task<CapacityDecision> TryReserveAsync(ReservationRequest request, CancellationToken ct);
    /// <summary>pending → held. Requires the observed state that justifies it.</summary>
    Task ConfirmAsync(IReadOnlyList<string> ids, VmState observed, CancellationToken ct);
    Task ReleaseAsync(IReadOnlyList<string> ids, VmState observed, string reason, CancellationToken ct);
    /// <summary>Trim a storage reservation to the artifact's real size (media completion).</summary>
    Task TrimAsync(string id, long amount, CancellationToken ct);
    /// <summary>Keeps a pending reservation alive while its operation runs (extends PendingUntil).</summary>
    Task ExtendAsync(IReadOnlyList<string> ids, TimeSpan by, CancellationToken ct);
    /// <summary>§4.4 sequence: snapshot without gates; per VM TryAcquire the VM gate (no wait, no upgrade) and only then the ledger gate; host-level rules under the ledger gate alone. Orphans are resolved per resource, never by time alone.</summary>
    Task<IReadOnlyList<OrphanOutcome>> ReconcileAsync(CancellationToken ct);
    Task<HostCapacitySnapshot> SnapshotAsync(bool refresh, CancellationToken ct);
}

/// <summary>Database half of reconciliation; each Apply call uses the same gate/transaction as admission.
/// The VM argument is the pre-inventory generation/job fence, never permission to write a stale snapshot.</summary>
public interface ICapacityReconciliationStore
{
    Task<IReadOnlyList<Reservation>> ReadReservationsAsync(CancellationToken ct);
    Task<IReadOnlyList<OrphanOutcome>> ApplyVmAsync(Vm expected, VmState freshState, InventorySnapshot inventory,
        IReadOnlyList<Reservation> captured, CancellationToken ct);
    Task<IReadOnlyList<OrphanOutcome>> ApplyHostAsync(InventorySnapshot inventory, IReadOnlyList<Reservation> captured, CancellationToken ct);
}
