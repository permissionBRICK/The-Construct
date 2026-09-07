namespace Constructd.Core.Domain;

/// <param name="OperationId">The operation (job id, or request id for synchronous starts) that owns a pending reservation.</param>
/// <param name="PendingUntil">Deadline after which an ORPHANED pending reservation (owner not alive) may be swept.</param>
public sealed record Reservation(
    string Id,
    ReservationResource Resource,
    string? ScopeOwner,
    string? VmName,
    string? Artifact,
    string? Volume,
    long Amount,
    ReservationPhase Phase,
    ReservationOrigin Origin,
    string? OperationId,
    DateTimeOffset Created,
    DateTimeOffset? PendingUntil,
    DateTimeOffset? ConfirmedAt);

public enum OrphanResolution { Released, PromotedToHeld, Kept }
public sealed record OrphanOutcome(string ReservationId, OrphanResolution Resolution, string Evidence);
