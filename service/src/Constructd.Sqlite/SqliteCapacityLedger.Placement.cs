using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Sqlite;

public sealed partial class SqliteCapacityLedger
{
    // Called only after the VM/incarnation/generation and live-operation fences in ApplyVmAsync.
    private void ResolvePrimaryPlacement(Transaction tx, Vm vm, HypervisorVmInfo? actual,
        InventorySnapshot inventory, IReadOnlyList<Reservation> captured)
    {
        if (vm.Kind != VmKind.Primary || actual is not { Complete: true } || !CapacityMath.Matches(vm, actual) ||
            inventory.Vms.Count(v => Ownership.SameName(v.Name, vm.Name)) != 1) return;
        bool VolumeKnown(string volume) => inventory.Host.Volumes.Any(v => CapacityMath.Key(v.Root) == CapacityMath.Key(volume));
        var owned = tx.Rows.Where(r => Ownership.SameName(r.VmName, vm.Name) && Ownership.SameName(r.ScopeOwner, vm.Owner)).ToArray();
        var unknown = owned.Where(r => r.Artifact == "unresolved-primary-disk:" + vm.Name).ToArray();
        // Primaries have one boot disk. A named boot disk also disambiguates an inventory with
        // extra attached disks; otherwise require exactly one disk rather than guess its identity.
        var named = actual.Disks.Where(d => Ownership.SameName(d.Path.Replace('\\', '/').Split('/').Last(), vm.Name + ".vhdx")).ToArray();
        var disk = named.Length == 1 ? named[0] : actual.Disks.Count == 1 ? actual.Disks[0] : null;
        if (unknown.Length > 0 && unknown.All(captured.Contains) && disk is { Readable: true } && VolumeKnown(disk.Volume))
        {
            var artifact = "disk:" + disk.Path;
            var known = tx.Rows.Where(r => r.Resource == ReservationResource.Storage && r.Artifact is not null &&
                CapacityMath.Key(r.Artifact) == CapacityMath.Key(artifact)).ToArray();
            if (known.All(r => owned.Contains(r) && captured.Contains(r)))
            {
                var amount = Math.Max(disk.MaxBytes, Math.Max(unknown.Sum(r => r.Amount), known.Sum(r => r.Amount)));
                foreach (var row in unknown.Concat(known)) tx.Delete(row.Id);
                // Put's conflict update intentionally does not change identity; delete first.
                tx.Put(unknown[0] with { Artifact = artifact, Volume = disk.Volume, Amount = amount,
                    Phase = ReservationPhase.Held, Origin = ReservationOrigin.Reconcile, ConfirmedAt = _clock.UtcNow });
                tx.Audit("capacity.placement-resolved", vm.Owner, vm.Name, "primary-disk");
            }
        }
        if (VolumeKnown(actual.ConfigVolume))
            foreach (var row in owned.Where(r => ReservationRules.SavedState(r) && r.Volume == "unknown" && captured.Contains(r)))
            {
                tx.Delete(row.Id);
                tx.Put(row with { Volume = actual.ConfigVolume });
                tx.Audit("capacity.placement-resolved", vm.Owner, vm.Name, "saved-state-volume");
            }
    }
}
