using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Sqlite;

public sealed partial class SqliteCapacityLedger
{
    private bool RecoverAbandonedChild(Transaction tx, Vm vm, VmState state, InventorySnapshot inventory)
    {
        if (vm.Kind != VmKind.Child || vm.CurrentJobId is null) return false;
        using var job = tx.Connection.CreateCommand(); job.Transaction = tx.Sql;
        job.CommandText = "SELECT COUNT(*) FROM jobs WHERE id=@id AND kind='child-create' AND state='Failed' AND phase IS NULL AND error IN ('interrupted by a service restart','Persisted job could not start.')";
        job.With("@id", vm.CurrentJobId);
        if (Convert.ToInt64(job.ExecuteScalar()) != 1) return false;
        var rows = tx.Rows.Where(r => Ownership.SameName(r.VmName, vm.Name)).ToArray();
        var disks = rows.Where(r => r.Artifact?.StartsWith("disk:", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        var absent = state == VmState.Absent && inventory.Complete &&
            !inventory.Vms.Any(v => Ownership.SameName(v.Name, vm.Name) || vm.Incarnation is not null && Ownership.SameName(v.Id, vm.Incarnation)) &&
            disks.Length > 0 && disks.All(r => Presence(r, inventory) == ArtifactPresence.Absent);
        using (var media = tx.Connection.CreateCommand())
        {
            media.Transaction = tx.Sql;
            media.CommandText = "SELECT path FROM media WHERE state <> 'ready' AND (dedicated_to=@name COLLATE NOCASE OR id IN (SELECT media_id FROM media_references WHERE vm_name=@name))";
            media.With("@name", vm.Name);
            using var reader = media.ExecuteReader();
            while (reader.Read())
                absent &= inventory.Artifacts?.Any(a => string.Equals(a.Artifact, reader.GetString(0), StringComparison.OrdinalIgnoreCase) && a.Presence == ArtifactPresence.Absent) == true;
        }
        using var update = tx.Connection.CreateCommand(); update.Transaction = tx.Sql; update.With("@name", vm.Name);
        if (absent)
        {
            foreach (var row in rows) tx.Delete(row.Id);
            update.CommandText = "DELETE FROM media_references WHERE vm_name=@name; DELETE FROM activity WHERE vm_name=@name; DELETE FROM vms WHERE name=@name";
            update.ExecuteNonQuery(); tx.Audit("vm.create.abandoned", vm.Owner, vm.Name, "absence-confirmed");
        }
        else
        {
            foreach (var row in rows) tx.Put(row with { Phase = ReservationPhase.Held, ConfirmedAt = _clock.UtcNow });
            update.CommandText = "UPDATE vms SET deleting=1 WHERE name=@name";
            update.ExecuteNonQuery();
            if (!vm.Deleting) tx.Audit("vm.create.abandoned", vm.Owner, vm.Name, "liability-retained");
        }
        tx.Commit(); return true;
    }
}
