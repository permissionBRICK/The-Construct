using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Sqlite;

public sealed partial class SqliteCapacityLedger : ICapacityReconciliationStore
{
    public IOperationRegistry? Operations { get; set; }
    public Task<IReadOnlyList<Reservation>> ReadReservationsAsync(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); using var c = database.Open(); return Task.FromResult(ReadRows(c, null)); }

    public async Task<IReadOnlyList<OrphanOutcome>> ApplyVmAsync(Vm expected, VmState freshState, InventorySnapshot inventory,
        IReadOnlyList<Reservation> captured, CancellationToken ct)
    {
        await using var tx = await BeginMutationAsync(ct);
        var current = tx.Vms.SingleOrDefault(v => Ownership.SameName(v.Name, expected.Name));
        if (current is null || current.PowerGeneration != expected.PowerGeneration || current.CurrentJobId != expected.CurrentJobId || current.Incarnation != expected.Incarnation)
            return [];
        if (Operations?.Alive().Any(op => Ownership.SameName(op.VmName, current.Name)) == true) return [];
        var preserveStartGeneration = false;
        using (var intent = tx.Connection.CreateCommand())
        {
            intent.Transaction = tx.Sql;
            intent.CommandText = "SELECT COUNT(*) FROM job_operation_keys WHERE target=@name COLLATE NOCASE AND state='inFlight' AND power_generation=@generation AND kind IN ('child-start','lifecycle-start','restart-start')";
            intent.With("@name", current.Name).With("@generation", current.PowerGeneration);
            preserveStartGeneration = freshState == VmState.Running && Convert.ToInt64(intent.ExecuteScalar()) > 0;
        }
        var actual = inventory.Vms.FirstOrDefault(v => CapacityMath.Matches(current, v));
        var mismatch = inventory.Vms.Any(v => Ownership.SameName(v.Name, current.Name) && !CapacityMath.Matches(current, v));
        // A renamed incarnation remains a liability. Incomplete enumeration is never absence evidence.
        var driverState = freshState;
        if (mismatch || actual is null && current.Incarnation is not null &&
            (!inventory.Complete || inventory.Vms.Any(v => Ownership.SameName(v.Id, current.Incarnation))))
            freshState = VmState.Unknown;
        var capturedIds = captured.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var outcomes = new List<OrphanOutcome>();
        foreach (var row in tx.Rows.Where(r => Ownership.SameName(r.VmName, current.Name) && capturedIds.Contains(r.Id)))
        {
            var before = captured.Single(r => r.Id == row.Id);
            // Confirmation/extension/trim since the pass began invalidates its artifact evidence too.
            if (before != row) continue;
            var presence = Presence(row, inventory);
            var outcome = ReservationRules.Resolve(row, freshState, row.OperationId is not null && Operations?.IsAlive(row.OperationId) == true, presence, _clock.UtcNow);
            if (ReservationRules.SavedState(row) && row.Phase == ReservationPhase.Held && freshState is VmState.Off or VmState.Absent)
                outcome = new(row.Id, OrphanResolution.Released, "observed-off");
            Apply(tx, row, outcome); outcomes.Add(outcome);
        }
        if (!mismatch)
        {
            void Ensure(ReservationResource resource, long amount, string? artifact = null, string? volume = null)
            {
                var existing = tx.Rows.Where(r => r.Resource == resource && (artifact is null ? Ownership.SameName(r.VmName, current.Name) :
                    r.Artifact is not null && (CapacityMath.Key(r.Artifact) == CapacityMath.Key(artifact) ||
                    artifact.StartsWith("saved-state:", StringComparison.OrdinalIgnoreCase) && ReservationRules.SavedState(r) && Ownership.SameName(r.VmName, current.Name)))).ToArray();
                var held = existing.Sum(r => r.Amount);
                if (held >= amount) return;
                var delta = amount - held;
                tx.Put(new(Guid.NewGuid().ToString("n"), resource, current.Owner, current.Name, artifact, volume, delta, ReservationPhase.Held,
                    ReservationOrigin.External, null, _clock.UtcNow, null, _clock.UtcNow));
                tx.Audit("capacity.external-hold", current.Owner, current.Name);
            }
            if (!ReservationRules.Terminal(freshState) && !ReservationRules.Terminal(driverState))
            {
                Ensure(ReservationResource.Ram, Math.Max(current.RamBytes, actual is null ? 0 : CapacityMath.Memory(actual)));
                Ensure(ReservationResource.Cpu, Math.Max(current.Cpu, actual?.Cpus ?? 0));
            }
            if (actual is not null)
            {
                foreach (var disk in actual.Disks) Ensure(ReservationResource.Storage, disk.MaxBytes, "disk:" + disk.Path, disk.Volume);
                if (freshState != VmState.Off && freshState != VmState.Absent)
                    Ensure(ReservationResource.Storage, checked(Math.Max(current.RamBytes, CapacityMath.Memory(actual)) + CapacityMath.SavedStateOverhead), "saved-state:" + actual.Id, actual.ConfigVolume);
            }
        }
        var unresolvedArtifact = tx.Rows.Any(r => Ownership.SameName(r.VmName, current.Name) && r.Resource == ReservationResource.Storage && r.Phase == ReservationPhase.Held &&
            capturedIds.Contains(r.Id) && Presence(r, inventory) != ArtifactPresence.Present &&
            !(ReservationRules.SavedState(r) && actual is not null && freshState is VmState.Running or VmState.Paused or VmState.Unknown or VmState.Off));
        var storageProblem = mismatch ? "incarnation-mismatch" : actual?.Complete == false || actual?.Disks.Any(d => !d.Readable) == true ? "disk-unreadable" : unresolvedArtifact ? "artifact-unreadable-or-missing" : null;
        using (var cmd = tx.Connection.CreateCommand())
        {
            cmd.Transaction = tx.Sql;
            // Unknown is not a confirmed transition. Never destroy the prior known state on a failed probe.
            cmd.CommandText = """
                UPDATE vms SET observed_storage_problem=@problem,
                  power_generation=power_generation + CASE WHEN @known=1 AND lower(state)<>@state THEN 1 ELSE 0 END,
                  state=CASE WHEN @known=1 THEN @state ELSE state END
                WHERE name=@name AND power_generation=@generation
                """;
            cmd.With("@name", current.Name).With("@generation", current.PowerGeneration).With("@problem", storageProblem)
                .With("@known", freshState != VmState.Unknown && !preserveStartGeneration).With("@state", WireJson.Enum(freshState)); cmd.ExecuteNonQuery();
        }
        if (!preserveStartGeneration && freshState != VmState.Unknown && freshState != current.State) tx.Audit("capacity.external-state", current.Owner, current.Name, WireJson.Enum(freshState));
        tx.Commit();
        // Never publish the pre-gate snapshot after observing a power transition.
        return outcomes;
    }

    public async Task<IReadOnlyList<OrphanOutcome>> ApplyHostAsync(InventorySnapshot inventory, IReadOnlyList<Reservation> captured, CancellationToken ct)
    {
        await using var tx = await BeginMutationAsync(ct);
        var outcomes = new List<OrphanOutcome>();
        foreach (var row in tx.Rows.Where(r => r.VmName is null && captured.Contains(r)))
        {
            var outcome = ReservationRules.Resolve(row, VmState.Unknown, row.OperationId is not null && Operations?.IsAlive(row.OperationId) == true,
                Presence(row, inventory), _clock.UtcNow);
            Apply(tx, row, outcome); outcomes.Add(outcome);
        }
        tx.Commit();
        // Publish for reporting, but do not call the hypervisor under this gate. Admission refreshes
        // after this pass: its inventory must reflect any concurrent or per-VM transitions.
        if (_snapshot is null || inventory.ObservedAt >= _snapshot.ObservedAt)
        { _snapshot = inventory; _readAt = _clock.UtcNow; }
        _invalidated = true;
        return outcomes;
    }
    private static ArtifactPresence Presence(Reservation row, InventorySnapshot inventory)
    {
        var evidence = inventory.Artifacts?.FirstOrDefault(a => row.Artifact is not null && CapacityMath.Key(a.Artifact) == CapacityMath.Key(row.Artifact));
        if (evidence is not null) return evidence.Presence;
        if (row.Artifact?.StartsWith("disk:", StringComparison.OrdinalIgnoreCase) == true && inventory.Vms.SelectMany(v => v.Disks).Any(d => d.Readable && CapacityMath.Key(d.Path) == CapacityMath.Key(row.Artifact[5..])))
            return ArtifactPresence.Present;
        return ArtifactPresence.Unknown;
    }
    private void Apply(Transaction tx, Reservation row, OrphanOutcome outcome)
    {
        if (outcome.Resolution == OrphanResolution.Released) tx.Delete(row.Id);
        else if (outcome.Resolution == OrphanResolution.PromotedToHeld)
            tx.Put(row with { Phase = ReservationPhase.Held, Origin = ReservationOrigin.Reconcile, ConfirmedAt = _clock.UtcNow });
        if (outcome.Resolution != OrphanResolution.Kept)
            tx.Audit(outcome.Resolution == OrphanResolution.Released ? "capacity.release" : "capacity.confirm", row.ScopeOwner, row.VmName ?? row.Id, outcome.Evidence);
    }
}
