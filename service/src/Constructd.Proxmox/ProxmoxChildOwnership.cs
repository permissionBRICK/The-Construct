using System.Text.Json;
using System.Text.RegularExpressions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Windows.Internal;

namespace Constructd.Proxmox;

public sealed partial class ProxmoxChildVmPlatform
{
    public async Task<string?> GetCreationOperationAsync(string name, CancellationToken ct)
    {
        ArgumentGuard.VmName(name);
        var owner = ReadOwnership(name);
        if (owner is null) return null;
        var resources = await commands.ResourcesAsync(ct);
        var id = commands.Find(resources, name);
        if (id is not null)
        {
            if (id != owner.VmId) throw Conflict();
            VerifyOwner(await commands.ConfigAsync(id.Value, ct), owner);
        }
        else EnsureIdAbsent(resources, owner.VmId);
        return owner.OperationId;
    }
    public async Task RemoveAsync(string name, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentGuard.VmName(name);
        var owner = ReadOwnership(name);
        var resources = await commands.ResourcesAsync(ct);
        var id = commands.Find(resources, name);
        if (owner is null)
        {
            if (id is not null) throw new ChildValidationException("artifact-ownership-unverified", "vm");
            return;
        }
        if (id is not null)
        {
            if (id != owner.VmId) throw Conflict();
            var config = await commands.ConfigAsync(id.Value, ct);
            VerifyOwner(config, owner);
            owner = owner with { Volumes = owner.Volumes.Concat(OwnedVolumes(config, id.Value)).Distinct().ToArray() };
            WriteOwnership(owner);
            progress?.Report("Removing child VM and owned disks.");
            var state = await commands.StateAsync(id.Value, ct);
            // Suspended QEMU is stopped; destroy removes its saved state itself.
            if (state is not (VmState.Off or VmState.Saved)) await commands.QmAsync(["stop", ProxmoxCommands.Number(id.Value)], ct);
            await commands.QmAsync(["destroy", ProxmoxCommands.Number(id.Value), "--purge", "1", "--destroy-unreferenced-disks", "1"], ct, TimeSpan.FromMinutes(30));
            resources = await commands.ResourcesAsync(ct);
        }
        EnsureIdAbsent(resources, owner.VmId);
        if (commands.Find(resources, name) is not null) throw Conflict();
        // A failed destroy may remove config before disks. Only journalled disks can be freed.
        var remaining = await commands.QueryAsync(["get", $"/nodes/{ProxmoxDriver.ResolveNode(options)}/storage/{Storage}/content", "--vmid", ProxmoxCommands.Number(owner.VmId)], ct);
        if (remaining.ValueKind != JsonValueKind.Array) throw ProxmoxCommands.Failure();
        foreach (var item in remaining.EnumerateArray())
        {
            var volume = ProxmoxCommands.String(item, "volid");
            if (volume is null || !owner.Volumes.Contains(volume, StringComparer.Ordinal))
                throw new ChildValidationException("artifact-ownership-unverified", "disk");
            var result = await commands.RunAsync(options.Proxmox.PvesmPath, ["free", volume], ct);
            if (!result.Succeeded) throw ProxmoxCommands.Failure();
        }
        File.Delete(OwnershipPath(name));
    }

    private static void EnsureIdAbsent(JsonElement resources, int id)
    {
        if (resources.ValueKind != JsonValueKind.Array) throw ProxmoxCommands.Failure();
        if (resources.EnumerateArray().Any(e => e.TryGetProperty("vmid", out var v) && v.TryGetInt32(out var n) && n == id)) throw Conflict();
    }
    private static void VerifyOwner(JsonElement config, ChildOwnership owner)
    {
        if (ProxmoxCommands.Incarnation(config) != owner.Incarnation || ProxmoxCommands.String(config, "name") != owner.Name) throw Conflict();
        var description = ProxmoxCommands.String(config, "description") ?? "";
        if (!ProxmoxCommands.HasChildTag(config) || !description.StartsWith("construct-child ", StringComparison.Ordinal) ||
            !description.Split(' ').Contains("operation=" + owner.OperationId, StringComparer.Ordinal) ||
            !description.Split(' ').Contains("uuid=" + owner.Incarnation, StringComparer.Ordinal))
            throw new ChildValidationException("artifact-ownership-unverified", "vm");
    }
    private string[] OwnedVolumes(JsonElement config, int id)
    {
        var volumes = new List<string>();
        foreach (var p in config.EnumerateObject())
        {
            if (!Regex.IsMatch(p.Name, @"\A(?:(?:scsi|sata|ide|virtio|efidisk|tpmstate|unused)\d+|vmstate)\z") || p.Value.ValueKind != JsonValueKind.String) continue;
            var value = p.Value.GetString()!;
            if (value.Split(',').Contains("media=cdrom", StringComparer.Ordinal)) continue;
            var volume = value.Split(',')[0];
            if (!volume.StartsWith(Storage + ":", StringComparison.Ordinal) ||
                !Regex.IsMatch(volume[(Storage.Length + 1)..], $@"\A(?:{id}/)?vm-{id}-(?:disk-\d+|state-[a-zA-Z0-9_.-]+)(?:\.(?:raw|qcow2|vmdk))?\z"))
                throw new ChildValidationException("artifact-ownership-unverified", "disk");
            volumes.Add(volume);
        }
        return volumes.ToArray();
    }
    private static ChildValidationException Conflict() => new("vm-incarnation-conflict", "vm");

    private sealed record ChildOwnership(string Name, int VmId, string Incarnation, string OperationId,
        string Parent, DateTimeOffset Created, string[] Volumes);
    private string OwnershipPath(string name) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!, "children", name + ".json");
    private ChildOwnership? ReadOwnership(string name)
    {
        var path = OwnershipPath(name);
        if (!File.Exists(path)) return null;
        try
        {
            var owner = JsonSerializer.Deserialize<ChildOwnership>(File.ReadAllText(path));
            if (owner is null || owner.Name != name || owner.VmId < 1 || !Guid.TryParse(owner.Incarnation, out _) || owner.Volumes is null)
                throw new ChildValidationException("artifact-ownership-unverified", "vm");
            return owner;
        }
        catch (JsonException) { throw new ChildValidationException("artifact-ownership-unverified", "vm"); }
    }
    private void WriteOwnership(ChildOwnership owner, bool create = false)
    {
        var path = OwnershipPath(owner.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, owner); stream.Flush(true); }
            File.Move(temporary, path, overwrite: !create);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
