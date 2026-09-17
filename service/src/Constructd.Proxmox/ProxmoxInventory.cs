using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;

namespace Constructd.Proxmox;

/// <summary>
/// The node's capacity picture for the ledger: logical CPUs and RAM from the node status, one
/// volume per active storage, and every QEMU VM on the node with its configured CPUs, RAM and disk
/// size. Three read-only <c>pvesh get</c> calls; any failure reports the same "inventory
/// unavailable" snapshot the Hyper-V reader does, which the ledger treats as incomplete evidence
/// rather than as an error.
/// </summary>
public sealed class ProxmoxInventory(IProcessRunner runner, ConstructdOptions options, IClock clock) : IHypervisorInventory
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);
    private long _epoch;

    public Task<InventorySnapshot> ReadAsync(CancellationToken ct) => ReadAsync([], ct);

    public async Task<InventorySnapshot> ReadAsync(IReadOnlyList<Reservation> reservations, CancellationToken ct)
    {
        var epoch = Interlocked.Increment(ref _epoch);
        // The oldest reading determines freshness, including the fence after a pressure save.
        var observedAt = clock.UtcNow;
        var node = ProxmoxDriver.ResolveNode(options);
        try
        {
            var status = await QueryAsync($"/nodes/{node}/status", ct).ConfigureAwait(false);
            if (status is null)
            {
                return Unavailable(epoch);
            }

            var storages = await QueryAsync($"/nodes/{node}/storage", ct).ConfigureAwait(false);
            if (storages is null)
            {
                return Unavailable(epoch);
            }

            var guests = await QueryAsync($"/nodes/{node}/qemu", ct).ConfigureAwait(false);
            if (guests is null)
            {
                return Unavailable(epoch);
            }

            var snapshot = Parse(epoch, observedAt, status.Value, storages.Value, guests.Value, options.Proxmox.Storage, reservations);
            return snapshot ?? Unavailable(epoch);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable(epoch);
        }
    }

    /// <summary>
    /// The snapshot out of the three answers, or null when a required number is missing. Pure.
    /// Storage reservations that name a VM disk (<c>disk:…</c>) get evidence: present with the
    /// guest's configured disk size when a VM of that name exists on the node, absent otherwise —
    /// the placement is decided before the VM has a numeric id, so the evidence is keyed by the
    /// reservation's own path rather than by the volume name Proxmox later assigns.
    /// </summary>
    public static InventorySnapshot? Parse(long epoch, DateTimeOffset now, JsonElement status, JsonElement storages, JsonElement guests, string diskStorage,
        IReadOnlyList<Reservation>? reservations = null)
    {
        if (!status.TryGetProperty("cpuinfo", out var cpuInfo) || !cpuInfo.TryGetProperty("cpus", out var cpusValue) ||
            !cpusValue.TryGetInt32(out var cpus) || cpus <= 0 ||
            !status.TryGetProperty("memory", out var memory) || !memory.TryGetProperty("total", out var totalValue) ||
            !totalValue.TryGetInt64(out var totalRam) || totalRam <= 0 ||
            !memory.TryGetProperty("free", out var freeValue) || !freeValue.TryGetInt64(out var freeRam) || freeRam < 0)
        {
            return null;
        }

        var volumes = new List<VolumeInfo>();
        if (storages.ValueKind == JsonValueKind.Array)
        {
            foreach (var storage in storages.EnumerateArray())
            {
                if (!storage.TryGetProperty("storage", out var id) || id.ValueKind != JsonValueKind.String ||
                    (storage.TryGetProperty("active", out var active) && active.TryGetInt32(out var isActive) && isActive == 0))
                {
                    continue;
                }

                var total = Long(storage, "total");
                var avail = Long(storage, "avail");
                if (total < 0 || avail < 0)
                {
                    continue;
                }

                volumes.Add(new VolumeInfo(id.GetString()!, total, avail));
            }
        }

        var vms = new List<HypervisorVmInfo>();
        if (guests.ValueKind == JsonValueKind.Array)
        {
            foreach (var guest in guests.EnumerateArray())
            {
                if (!guest.TryGetProperty("vmid", out var vmidValue) || !vmidValue.TryGetInt32(out var vmid))
                {
                    continue;
                }

                var name = guest.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                    ? nameValue.GetString()!
                    : vmid.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var raw = guest.TryGetProperty("status", out var statusValue) && statusValue.ValueKind == JsonValueKind.String
                    ? statusValue.GetString()!
                    : "unknown";
                var state = raw switch { "running" => VmState.Running, "stopped" => VmState.Off, _ => VmState.Unknown };
                var vmCpus = (int)Math.Max(1, Long(guest, "cpus"));
                var maxMem = Math.Max(0, Long(guest, "maxmem"));
                var maxDisk = Math.Max(0, Long(guest, "maxdisk"));
                var cpuUsage = guest.TryGetProperty("cpu", out var cpuValue) && cpuValue.TryGetDouble(out var cpu) ? cpu * 100 : (double?)null;
                var uptime = guest.TryGetProperty("uptime", out var uptimeValue) && uptimeValue.TryGetDouble(out var up) ? up : (double?)null;
                var demand = Long(guest, "mem");

                vms.Add(new HypervisorVmInfo(
                    name,
                    vmid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    state,
                    raw,
                    Generation: 2,
                    vmCpus,
                    MemoryStartupBytes: maxMem,
                    MemoryAssignedBytes: state == VmState.Running ? maxMem : 0,
                    DynamicMemory: false,
                    MemoryMaximumBytes: null,
                    Disks: [new HypervisorDiskInfo($"{diskStorage}:vm-{vmid}-disk-0", maxDisk, maxDisk, null, diskStorage, Readable: true)],
                    SavedStateBytes: null,
                    // Suspend-to-disk state lands on the VM's disk storage; the ledger accounts it there.
                    ConfigVolume: diskStorage,
                    Complete: true,
                    CpuUsagePercent: cpuUsage,
                    MemoryDemandBytes: demand >= 0 ? demand : null,
                    UptimeSeconds: uptime));
            }
        }

        var artifacts = new List<CapacityArtifactInfo>();
        foreach (var row in reservations ?? [])
        {
            if (row.Resource != ReservationResource.Storage || row.Artifact is null ||
                !row.Artifact.StartsWith("disk:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = row.Artifact[5..];
            var owner = vms.FirstOrDefault(v => string.Equals(v.Name, row.VmName, StringComparison.OrdinalIgnoreCase));
            artifacts.Add(owner is null
                ? new CapacityArtifactInfo(row.Artifact, path, row.Volume ?? diskStorage, 0, ArtifactPresence.Absent)
                : new CapacityArtifactInfo(row.Artifact, path, row.Volume ?? diskStorage, owner.Disks[0].FileBytes, ArtifactPresence.Present));
        }

        var usedRam = Long(memory, "used");
        var swapTotal = status.TryGetProperty("swap", out var swap) && swap.ValueKind == JsonValueKind.Object ? Long(swap, "total") : -1;
        var swapUsed = swap.ValueKind == JsonValueKind.Object ? Long(swap, "used") : -1;
        return new InventorySnapshot(epoch, now, new HostResourcesInfo(cpus, totalRam, freeRam, volumes, now,
            usedRam >= 0 ? usedRam : null, swapTotal >= 0 ? swapTotal : null, swapUsed >= 0 ? swapUsed : null), vms, Complete: true, Problems: [], artifacts);
    }

    private async Task<JsonElement?> QueryAsync(string path, CancellationToken ct)
    {
        var result = await runner.RunAsync(options.Proxmox.PveshPath, ["get", path, "--output-format", "json"],
            standardInput: null, QueryTimeout, standardOutputLines: null, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.Clone();
    }

    private static long Long(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : -1;

    private InventorySnapshot Unavailable(long epoch) =>
        new(epoch, clock.UtcNow, new(0, 0, 0, [], clock.UtcNow), [], false, ["inventory-unavailable"]);
}
