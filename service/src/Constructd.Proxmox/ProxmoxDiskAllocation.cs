using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Proxmox;

public sealed partial class ProxmoxInventory
{
    private static readonly TimeSpan AllocationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StorageCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly Regex VmDiskVolumeName = new(@"^vm-(\d+)-disk-", RegexOptions.CultureInvariant);

    private volatile StorageDefinitionCache? _storageCache;

    private sealed record DiskSource(int VmId, bool FromConfig, IReadOnlyList<string> NativeVolumes);
    private sealed record StorageDefinition(string Name, string Type, string? Pool, string? VgName, string? ThinPool, string? Path);
    private sealed record StorageDefinitionCache(DateTimeOffset LoadedAt, IReadOnlyDictionary<string, StorageDefinition> ByName);

    private async Task<InventorySnapshot> ApplyDiskAllocationsAsync(
        InventorySnapshot snapshot,
        JsonElement storages,
        IReadOnlyDictionary<int, DiskSource> diskSources,
        IReadOnlyList<Reservation> reservations,
        CancellationToken ct)
    {
        if (snapshot.Vms.Count == 0 || !snapshot.Vms.Any(v => v.Disks.Count > 0))
        {
            return snapshot;
        }

        var storageTypes = ParseNodeStorageTypes(storages);
        var neededStorages = snapshot.Vms
            .SelectMany(v => v.Disks)
            .Select(d => d.Volume)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (neededStorages.Length == 0)
        {
            return snapshot;
        }

        var definitions = await GetStorageDefinitionsAsync(neededStorages, ct).ConfigureAwait(false);
        var exactAllocations = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var vmDiskTotals = new Dictionary<(string Storage, int VmId), long>();
        var thickStorages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var storageName in neededStorages)
        {
            definitions.TryGetValue(storageName, out var def);
            var type = def?.Type ?? (storageTypes.TryGetValue(storageName, out var nodeType) ? nodeType : null);
            if (string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            if (string.Equals(type, "zfspool", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(def?.Pool))
                {
                    await ReadZfsAllocationsAsync(storageName, def.Pool, exactAllocations, vmDiskTotals, ct).ConfigureAwait(false);
                }
            }
            else if (string.Equals(type, "lvmthin", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(def?.VgName))
                {
                    await ReadLvmThinAllocationsAsync(storageName, def.VgName, def.ThinPool, exactAllocations, vmDiskTotals, ct).ConfigureAwait(false);
                }
            }
            else if (string.Equals(type, "lvm", StringComparison.OrdinalIgnoreCase))
            {
                thickStorages.Add(storageName);
            }
            else if (IsFileStorage(type))
            {
                if (!string.IsNullOrWhiteSpace(def?.Path))
                {
                    await ReadFileAllocationsAsync(storageName, def.Path, snapshot.Vms, diskSources, exactAllocations, ct).ConfigureAwait(false);
                }
            }
            else if (storageTypes.ContainsKey(storageName))
            {
                await ReadPveshVolumeAllocationsAsync(storageName, snapshot.Vms, diskSources, exactAllocations, ct).ConfigureAwait(false);
            }
        }

        var updatedVms = new List<HypervisorVmInfo>(snapshot.Vms.Count);
        for (var i = 0; i < snapshot.Vms.Count; i++)
        {
            var vm = snapshot.Vms[i];
            diskSources.TryGetValue(i, out var source);
            var updatedDisks = new List<HypervisorDiskInfo>(vm.Disks.Count);
            for (var j = 0; j < vm.Disks.Count; j++)
            {
                var disk = vm.Disks[j];
                var nativeVol = source is not null && j < source.NativeVolumes.Count ? source.NativeVolumes[j] : disk.Path;
                if (TryResolveAllocation(disk, nativeVol, source, exactAllocations, vmDiskTotals, thickStorages, out var allocated))
                {
                    updatedDisks.Add(disk with { FileBytes = allocated, AllocationKnown = true });
                }
                else
                {
                    updatedDisks.Add(disk with { FileBytes = 0, AllocationKnown = false });
                }
            }

            updatedVms.Add(vm with { Disks = updatedDisks });
        }

        var updatedArtifacts = snapshot.Artifacts?.ToList();
        if (updatedArtifacts is { Count: > 0 })
        {
            for (var i = 0; i < updatedArtifacts.Count; i++)
            {
                if (updatedArtifacts[i].Presence != ArtifactPresence.Present)
                {
                    continue;
                }

                var row = reservations.FirstOrDefault(r => string.Equals(r.Artifact, updatedArtifacts[i].Artifact, StringComparison.OrdinalIgnoreCase));
                if (row is null)
                {
                    continue;
                }

                var owner = updatedVms.FirstOrDefault(v => string.Equals(v.Name, row.VmName, StringComparison.OrdinalIgnoreCase));
                if (owner is not null && owner.Disks.Count > 0)
                {
                    updatedArtifacts[i] = updatedArtifacts[i] with { FileBytes = owner.Disks[0].FileBytes };
                }
            }
        }

        return snapshot with { Vms = updatedVms, Artifacts = updatedArtifacts };
    }

    private static bool TryResolveAllocation(
        HypervisorDiskInfo disk,
        string nativeVol,
        DiskSource? source,
        IReadOnlyDictionary<string, long> exactAllocations,
        IReadOnlyDictionary<(string Storage, int VmId), long> vmDiskTotals,
        IReadOnlySet<string> thickStorages,
        out long allocated)
    {
        if (thickStorages.Contains(disk.Volume))
        {
            allocated = disk.MaxBytes;
            return true;
        }

        if (source is { FromConfig: false } && vmDiskTotals.TryGetValue((disk.Volume.ToUpperInvariant(), source.VmId), out allocated))
        {
            return true;
        }

        if (exactAllocations.TryGetValue(nativeVol, out allocated))
        {
            return true;
        }

        // Linked clones on ZFS use `storage:base-X-disk-0/vm-Y-disk-0` in config while the zvol name is `vm-Y-disk-0`.
        var colon = nativeVol.IndexOf(':');
        if (colon > 0)
        {
            var rel = nativeVol[(colon + 1)..];
            var slash = rel.LastIndexOf('/');
            if (slash >= 0 && slash + 1 < rel.Length)
            {
                var leafVol = $"{nativeVol[..colon]}:{rel[(slash + 1)..]}";
                if (exactAllocations.TryGetValue(leafVol, out allocated))
                {
                    return true;
                }
            }
        }

        allocated = 0;
        return false;
    }

    private static Dictionary<string, string> ParseNodeStorageTypes(JsonElement storages)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (storages.ValueKind != JsonValueKind.Array)
        {
            return map;
        }

        foreach (var item in storages.EnumerateArray())
        {
            var name = ProxmoxCommands.String(item, "storage");
            var type = ProxmoxCommands.String(item, "type");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            if (item.TryGetProperty("active", out var active) && active.TryGetInt32(out var isActive) && isActive == 0)
            {
                continue;
            }

            map[name] = type;
        }

        return map;
    }

    private async Task<IReadOnlyDictionary<string, StorageDefinition>> GetStorageDefinitionsAsync(
        IReadOnlyList<string> neededStorages,
        CancellationToken ct)
    {
        var now = clock.UtcNow;
        var cached = _storageCache;
        if (cached is not null && now - cached.LoadedAt < StorageCacheTtl &&
            neededStorages.All(s => cached.ByName.ContainsKey(s)))
        {
            return cached.ByName;
        }

        var response = await QueryAsync("/storage", ct).ConfigureAwait(false);
        if (response is null || response.Value.ValueKind != JsonValueKind.Array)
        {
            return cached?.ByName ?? new Dictionary<string, StorageDefinition>(StringComparer.OrdinalIgnoreCase);
        }

        var map = new Dictionary<string, StorageDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in response.Value.EnumerateArray())
        {
            var name = ProxmoxCommands.String(item, "storage");
            var type = ProxmoxCommands.String(item, "type");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            map[name] = new StorageDefinition(
                name,
                type,
                ProxmoxCommands.String(item, "pool"),
                ProxmoxCommands.String(item, "vgname"),
                ProxmoxCommands.String(item, "thinpool"),
                ProxmoxCommands.String(item, "path"));
        }

        _storageCache = new StorageDefinitionCache(now, map);
        return map;
    }

    private async Task ReadZfsAllocationsAsync(
        string storageName,
        string pool,
        IDictionary<string, long> exactAllocations,
        IDictionary<(string Storage, int VmId), long> vmDiskTotals,
        CancellationToken ct)
    {
        ProcessResult result;
        try
        {
            result = await runner.RunAsync(
                options.Proxmox.ZfsPath,
                ["list", "-Hp", "-o", "name,usedbydataset", "-t", "volume", "-r", pool],
                standardInput: null, AllocationTimeout, standardOutputLines: null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return; }

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return;
        }

        var prefix = pool.TrimEnd('/') + "/";
        foreach (var rawLine in result.StandardOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            var dataset = parts[0];
            if (!dataset.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var used) || used < 0) continue;

            var volName = dataset[prefix.Length..];
            exactAllocations[$"{storageName}:{volName}"] = used;
            AddVmDiskTotal(storageName, volName, used, vmDiskTotals);
        }
    }

    private async Task ReadLvmThinAllocationsAsync(
        string storageName,
        string vgName,
        string? thinPool,
        IDictionary<string, long> exactAllocations,
        IDictionary<(string Storage, int VmId), long> vmDiskTotals,
        CancellationToken ct)
    {
        ProcessResult result;
        try
        {
            result = await runner.RunAsync(
                options.Proxmox.LvsPath,
                ["--noheadings", "--nosuffix", "--units", "b", "--separator", "|", "-o", "lv_name,lv_size,data_percent,pool_lv", vgName],
                standardInput: null, AllocationTimeout, standardOutputLines: null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return; }

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return;
        }

        foreach (var rawLine in result.StandardOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split('|');
            if (parts.Length < 4) continue;

            var lvName = parts[0].Trim();
            var poolLv = parts[3].Trim();
            if (!string.IsNullOrWhiteSpace(thinPool) && !string.Equals(poolLv, thinPool, StringComparison.Ordinal)) continue;
            if (!long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lvSize) || lvSize < 0) continue;
            if (!decimal.TryParse(parts[2].Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var dataPercent) || dataPercent < 0) continue;

            var allocated = Math.Clamp((long)Math.Round(lvSize * (dataPercent / 100m), MidpointRounding.AwayFromZero), 0, lvSize);
            exactAllocations[$"{storageName}:{lvName}"] = allocated;
            AddVmDiskTotal(storageName, lvName, allocated, vmDiskTotals);
        }
    }

    private async Task ReadFileAllocationsAsync(
        string storageName,
        string storagePath,
        IReadOnlyList<HypervisorVmInfo> vms,
        IReadOnlyDictionary<int, DiskSource> diskSources,
        IDictionary<string, long> exactAllocations,
        CancellationToken ct)
    {
        var pathToVolId = new Dictionary<string, string>(StringComparer.Ordinal);
        var args = new List<string> { "-c", "%b|%n" };

        for (var i = 0; i < vms.Count; i++)
        {
            if (!diskSources.TryGetValue(i, out var source) || !source.FromConfig)
            {
                continue;
            }

            for (var j = 0; j < vms[i].Disks.Count && j < source.NativeVolumes.Count; j++)
            {
                var disk = vms[i].Disks[j];
                if (!string.Equals(disk.Volume, storageName, StringComparison.OrdinalIgnoreCase)) continue;
                var volId = source.NativeVolumes[j];
                var colon = volId.IndexOf(':');
                if (colon <= 0 || colon + 1 >= volId.Length) continue;
                var rel = volId[(colon + 1)..];
                if (!TryBuildPosixImagePath(storagePath, rel, out var full)) continue;
                if (pathToVolId.TryAdd(full, volId))
                {
                    args.Add(full);
                }
            }
        }

        if (pathToVolId.Count == 0)
        {
            return;
        }

        ProcessResult result;
        try
        {
            result = await runner.RunAsync(
                options.Proxmox.StatPath,
                args,
                standardInput: null, AllocationTimeout, standardOutputLines: null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return; }

        if (result.TimedOut || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return;
        }

        foreach (var rawLine in result.StandardOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var sep = line.IndexOf('|');
            if (sep <= 0 || sep + 1 >= line.Length) continue;
            if (!long.TryParse(line[..sep], NumberStyles.Integer, CultureInfo.InvariantCulture, out var blocks) || blocks < 0) continue;
            var path = line[(sep + 1)..];
            if (pathToVolId.TryGetValue(path, out var volId))
            {
                exactAllocations[volId] = checked(blocks * 512L);
            }
        }
    }

    private async Task ReadPveshVolumeAllocationsAsync(
        string storageName,
        IReadOnlyList<HypervisorVmInfo> vms,
        IReadOnlyDictionary<int, DiskSource> diskSources,
        IDictionary<string, long> exactAllocations,
        CancellationToken ct)
    {
        var node = ProxmoxDriver.ResolveNode(options);
        var queried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < vms.Count; i++)
        {
            if (!diskSources.TryGetValue(i, out var source) || !source.FromConfig)
            {
                continue;
            }

            for (var j = 0; j < vms[i].Disks.Count && j < source.NativeVolumes.Count; j++)
            {
                var disk = vms[i].Disks[j];
                if (!string.Equals(disk.Volume, storageName, StringComparison.OrdinalIgnoreCase)) continue;
                var volId = source.NativeVolumes[j];
                if (!queried.Add(volId)) continue;

                var info = await QueryAsync($"/nodes/{node}/storage/{storageName}/content/{Uri.EscapeDataString(volId)}", ct).ConfigureAwait(false);
                if (info is null || info.Value.ValueKind != JsonValueKind.Object) continue;
                var used = Long(info.Value, "used");
                if (used >= 0)
                {
                    exactAllocations[volId] = used;
                }
            }
        }
    }

    private static bool IsFileStorage(string type) =>
        type.Equals("dir", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("nfs", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("cifs", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("btrfs", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("glusterfs", StringComparison.OrdinalIgnoreCase);

    private static bool TryBuildPosixImagePath(string storagePath, string relativeVolume, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(storagePath) || !storagePath.StartsWith('/') || storagePath.Contains('\0'))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(relativeVolume) || relativeVolume.StartsWith('/') ||
            relativeVolume.Contains('\\') || relativeVolume.Contains('\0'))
        {
            return false;
        }

        var segments = relativeVolume.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }
        }

        fullPath = $"{storagePath.TrimEnd('/')}/images/{string.Join('/', segments)}";
        return true;
    }

    private static void AddVmDiskTotal(string storageName, string volName, long used, IDictionary<(string Storage, int VmId), long> vmDiskTotals)
    {
        var match = VmDiskVolumeName.Match(volName);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vmId))
        {
            return;
        }

        var key = (storageName.ToUpperInvariant(), vmId);
        vmDiskTotals[key] = vmDiskTotals.TryGetValue(key, out var existing) ? existing + used : used;
    }
}
