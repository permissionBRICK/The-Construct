using System.Globalization;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Proxmox;

public sealed partial class ProxmoxInventory
{
    // Keep the primary inventory parser unchanged. Child identities are SMBIOS UUIDs, and disk-0
    // is usually EFI variables, not the guest disk. Resolve those facts from the tagged config.
    private async Task<InventorySnapshot> WithChildrenAsync(InventorySnapshot snapshot, JsonElement guests,
        IReadOnlyList<Reservation> reservations, CancellationToken ct)
    {
        var vms = snapshot.Vms.ToList(); var artifacts = snapshot.Artifacts!.ToList();
        var commands = new ProxmoxCommands(runner, options);
        foreach (var guest in guests.EnumerateArray().Where(g => ProxmoxCommands.HasChildTag(g) ||
                     (ProxmoxCommands.String(g, "tags") ?? "").Split(';').Contains("construct")))
        {
            var id = guest.GetProperty("vmid").GetInt32();
            var index = vms.FindIndex(vm => vm.Id == ProxmoxCommands.Number(id));
            if (index < 0) continue;
            var old = vms[index];
            var config = await commands.ConfigAsync(id, ct);
            var uuid = ProxmoxCommands.Incarnation(config);
            if (uuid is null || ProxmoxCommands.String(config, "name") != old.Name) throw ProxmoxCommands.Failure();
            // PrimaryVmJobs also records GetVmIdAsync now that the child platform supplies it.
            // Its inventory identity must agree, or capacity would count a managed primary twice.
            if (!ProxmoxCommands.HasChildTag(guest))
            {
                vms[index] = old with { Id = uuid };
                continue;
            }
            var description = ProxmoxCommands.String(config, "description") ?? "";
            if (!ProxmoxCommands.HasChildTag(config) ||
                !description.StartsWith("construct-child ", StringComparison.Ordinal) ||
                !description.Split(' ').Contains("uuid=" + uuid, StringComparer.Ordinal))
                throw ProxmoxCommands.Failure();
            var state = await commands.StateAsync(id, ct);
            var disks = new List<HypervisorDiskInfo>();
            foreach (var slot in new[] { "scsi0", "efidisk0", "tpmstate0" })
            {
                var text = ProxmoxCommands.String(config, slot);
                if (text is null) { if (slot == "scsi0") throw ProxmoxCommands.Failure(); else continue; }
                var volume = text.Split(',')[0]; var storage = volume.Split(':')[0];
                if (!volume.Contains(':')) throw ProxmoxCommands.Failure();
                var size = Bytes(ProxmoxCommands.Property(text, "size")) ?? throw ProxmoxCommands.Failure();
                // Config gives virtual size, never thin allocation. Reserve its full possible growth
                // until an allocation-aware storage reader is available.
                disks.Add(new(volume, size, 0, null, storage, true));
            }
            long? savedBytes = null;
            var savedVolume = ProxmoxCommands.String(config, "vmstate")?.Split(',')[0];
            if (savedVolume is not null)
            {
                var content = await commands.QueryAsync(["get", $"/nodes/{ProxmoxDriver.ResolveNode(options)}/storage/{savedVolume.Split(':')[0]}/content",
                    "--vmid", ProxmoxCommands.Number(id)], ct);
                foreach (var item in content.EnumerateArray())
                    if (ProxmoxCommands.String(item, "volid") == savedVolume && Long(item, "size") >= 0) savedBytes = Long(item, "size");
            }
            vms[index] = old with { Id = uuid, State = state, RawState = state.ToString(), Disks = disks,
                SavedStateBytes = savedBytes, ConfigVolume = savedVolume?.Split(':')[0] ?? disks[0].Volume,
                MemoryAssignedBytes = state is VmState.Running or VmState.Paused ? old.MemoryStartupBytes : 0 };
            for (var i = 0; i < artifacts.Count; i++)
            {
                var row = reservations.First(r => r.Artifact == artifacts[i].Artifact);
                if (string.Equals(row.VmName, old.Name, StringComparison.OrdinalIgnoreCase))
                    artifacts[i] = artifacts[i] with { Path = disks[0].Path, Volume = disks[0].Volume, FileBytes = 0 };
            }
        }
        // A failed create/destroy can leave journalled disks after the VM config disappears.
        // Retain the reservation until the child remover has confirmed and removed those artifacts.
        for (var i = 0; i < artifacts.Count; i++)
        {
            var row = reservations.First(r => r.Artifact == artifacts[i].Artifact);
            if (row.VmName is { } name && name == Path.GetFileName(name) && artifacts[i].Presence == ArtifactPresence.Absent &&
                File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!, "children", name + ".json")))
                artifacts[i] = artifacts[i] with { Presence = ArtifactPresence.Unknown };
        }
        return snapshot with { Vms = vms, Artifacts = artifacts };
    }
    private static long? Bytes(string? size)
    {
        if (string.IsNullOrEmpty(size)) return null;
        var factor = size[^1] switch { 'T' => 1L << 40, 'G' => 1L << 30, 'M' => 1L << 20, 'K' => 1L << 10, _ => 1L };
        return decimal.TryParse(factor == 1 ? size : size[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) &&
            value >= 0 && value <= long.MaxValue / factor ? (long)(value * factor) : null;
    }
}
