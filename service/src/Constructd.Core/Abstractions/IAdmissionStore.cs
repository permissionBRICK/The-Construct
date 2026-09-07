using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

/// <summary>
/// Everything an accepting request must write together. The SQLite implementation executes the
/// whole plan in ONE IMMEDIATE transaction under the ledger gate; the in-memory one under one lock.
/// Nothing in the plan is visible to anybody until the transaction commits.
/// </summary>
public sealed record AdmissionPlan(
    OperationKeyRecord? OperationKey,
    Vm? VmToInsert,
    EffectiveAllowance? Allowance,
    IReadOnlyList<MediaItem> MediaToInsert,
    IReadOnlyList<MediaUpload> UploadsToInsert,
    IReadOnlyList<MediaReference> ReferencesToInsert,
    ReservationRequest? Reservation,
    CascadePreview? CascadeToAccept,
    Job? JobToInsert,
    string? VmToFence,
    string? FenceJobId,
    bool CloseChildCreation,
    string? VmToAssignJob = null);

public enum AdmissionOutcome { Accepted, Replay, KeyConflict, VersionConflict, NameTaken, QuotaExceeded, ParentClosed, ParentMissing, MediaNotReady, CapacityRefused, CascadeMismatch }

public sealed record AdmissionResult(
    AdmissionOutcome Outcome,
    OperationKeyRecord? ExistingKey,
    CapacityDecision? Capacity,
    CascadeAcceptance? Cascade,
    IReadOnlyList<string> ReservationIds);

public interface IAdmissionStore
{
    /// <summary>The caller already holds the maintenance-gate handle (§7.4); the plan commits everything or nothing.</summary>
    Task<AdmissionResult> AdmitAsync(AdmissionPlan plan, CancellationToken ct);
    /// <summary>
    /// ONE transaction over the scope. Used for database-only synchronous mutations (sharing, renew,
    /// overrides, allowances: the mutation plus the Completed key with its response) AND for the
    /// database half of an external mutation after the hypervisor call (lease activation + reservation
    /// confirm + key completion + power generation, §7.3). The scope itself performs no I/O outside SQLite.
    /// A null <paramref name="key"/> means "no operation key" (today's path). Runs UNDER THE LEDGER GATE
    /// (the same non-reentrant gate as AdmitAsync): callers hold VM/media gates, never the ledger gate,
    /// when they call it; reconciliation's per-VM step IS a MutateAsync call (§4.4).
    /// </summary>
    Task<AdmissionResult> MutateAsync(OperationKeyRecord? key, Func<IAdmissionScope, Task<bool>> mutation, CancellationToken ct);
    /// <summary>
    /// When the persisted job cannot be started (in-process failure after commit): the job row is marked
    /// failed with <paramref name="error"/>; every fence, reservation and row the plan created is then
    /// recovered by the SAME rules as a crashed job (§4.4, §8.8), never by an ad-hoc delete.
    /// </summary>
    Task MarkStartFailedAsync(string jobId, string error, CancellationToken ct);
}

/// <summary>What a database-only mutation may write inside MutateAsync (all on the same transaction).</summary>
public interface IAdmissionScope
{
    Task<bool> UpdateLeaseAsync(string vmName, Lease lease, long expectedVersion);
    Task<bool> UpdateSharingAsync(string vmName, SharingScope scope);
    Task SetOverrideAsync(VmOverride value);
    Task<bool> SetAllowanceAsync(string userName, UserAllowance allowance);
    /// <summary>Compare-and-bump of the VM's power generation (§5.3b); false when it moved.</summary>
    Task<bool> UpdatePowerStateAsync(string vmName, VmState state, long expectedGeneration);
    Task<bool> BumpPowerGenerationAsync(string vmName, long expected);
    /// <summary>The VM row as it is INSIDE this transaction (fresh, gate-protected read for §4.4 staleness checks).</summary>
    Task<Vm?> ReadVmAsync(string vmName);
    /// <summary>Re-admission of a start whose reservations were swept (§7.3): same rules as AdmitAsync, inside this transaction. A refusal must not mark the scope conflicted: the caller may commit the refusal response atomically, or return false to roll back.</summary>
    Task<CapacityDecision> ReserveAsync(ReservationRequest request);
    Task ConfirmReservationsAsync(IReadOnlyList<string> ids, VmState observed);
    Task ReleaseReservationsAsync(IReadOnlyList<string> ids, VmState observed, string reason);
    Task<bool> CompleteOperationKeyAsync(string owner, string kind, string key, string responseJson);
    Task AppendAuditAsync(AuditEntry entry);
}
