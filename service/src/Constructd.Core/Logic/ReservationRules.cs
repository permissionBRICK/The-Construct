using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

public static class ReservationRules
{
    public static bool Terminal(VmState state) => state is VmState.Off or VmState.Saved or VmState.Absent;
    public static bool SavedState(Reservation row) => row.Artifact?.StartsWith("saved-state:", StringComparison.OrdinalIgnoreCase) == true;
    public static bool CanConfirm(Reservation row, VmState state) => row.Resource == ReservationResource.Storage || !Terminal(state);
    // Absent is artifact-deletion evidence only when supplied by the owning cleanup operation.
    public static bool CanRelease(Reservation row, VmState state) => row.Resource != ReservationResource.Storage
        ? Terminal(state) : SavedState(row) ? state is VmState.Off or VmState.Absent : state == VmState.Absent;

    public static OrphanOutcome Resolve(Reservation row, VmState state, bool alive, ArtifactPresence artifact, DateTimeOffset now)
    {
        if (alive) return new(row.Id, OrphanResolution.Kept, "operation-alive");
        if (row.Resource == ReservationResource.Storage)
        {
            if (row.Phase != ReservationPhase.Pending || row.PendingUntil is null || row.PendingUntil >= now)
                return new(row.Id, OrphanResolution.Kept, "storage-retained");
            return new(row.Id, artifact switch { ArtifactPresence.Present => OrphanResolution.PromotedToHeld,
                ArtifactPresence.Absent => OrphanResolution.Released, _ => OrphanResolution.Kept },
                artifact switch { ArtifactPresence.Present => "artifact-present", ArtifactPresence.Absent => "artifact-absent", _ => "artifact-unknown" });
        }
        if (!Terminal(state)) return new(row.Id, row.Phase == ReservationPhase.Pending ? OrphanResolution.PromotedToHeld : OrphanResolution.Kept, "observed-active-or-unknown");
        return new(row.Id, row.Phase == ReservationPhase.Held || row.PendingUntil < now ? OrphanResolution.Released : OrphanResolution.Kept,
            "observed-" + state.ToString().ToLowerInvariant());
    }

    public static void Validate(ReservationRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId);
        if (request.PendingTimeout <= TimeSpan.Zero) throw new ArgumentException("Pending timeout must be positive.");
        foreach (var line in request.Lines)
        {
            if (!Enum.IsDefined(line.Resource) || line.Amount < 0 || line.Resource == ReservationResource.Cpu && line.Amount > int.MaxValue)
                throw new ArgumentException("Invalid reservation amount or resource.");
            if (line.Resource != ReservationResource.Storage && string.IsNullOrWhiteSpace(request.VmName))
                throw new ArgumentException("Runtime reservations require a VM name.");
            if (line.Resource == ReservationResource.Storage && (string.IsNullOrWhiteSpace(line.Artifact) || string.IsNullOrWhiteSpace(line.Volume)))
                throw new ArgumentException("Storage requires an artifact and volume.");
        }
        var artifacts = request.Lines.Where(l => l.Resource == ReservationResource.Storage).Select(l => CapacityMath.Key(l.Artifact!)).ToArray();
        if (artifacts.Distinct(StringComparer.Ordinal).Count() != artifacts.Length) throw new ArgumentException("An artifact may only be reserved once.");
        // Check overflow before any write, including observe mode.
        foreach (var group in request.Lines.GroupBy(l => l.Resource)) _ = group.Sum(l => l.Amount);
    }
}
