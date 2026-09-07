using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

/// <summary>One epoch, growth-only storage, and the lesser of physical and model RAM.</summary>
public static class CapacityMath
{
    public const long SavedStateOverhead = 64L << 20;
    public static string Key(string value) => value.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
    public static bool Matches(Vm vm, HypervisorVmInfo actual) => Ownership.SameName(vm.Name, actual.Name)
        && (vm.Incarnation is null || Ownership.SameName(vm.Incarnation, actual.Id));
    public static long Memory(HypervisorVmInfo vm) => Math.Max(Math.Max(vm.MemoryStartupBytes, vm.MemoryAssignedBytes),
        vm.DynamicMemory ? vm.MemoryMaximumBytes ?? 0 : 0);

    public static IReadOnlyList<Reservation> AccountedReservations(InventorySnapshot inventory,
        IReadOnlyList<Reservation> reservations, IReadOnlyList<Vm> managed)
    {
        var accounted = reservations.ToList();
        foreach (var vm in managed)
        {
            var actual = inventory.Vms.FirstOrDefault(a => Matches(vm, a));
            if (actual is null) continue;
            void Hold(ReservationResource resource, long amount, string? artifact = null, string? volume = null)
            {
                var used = artifact is null ? accounted.Where(r => r.Resource == resource && Ownership.SameName(r.VmName, vm.Name)).Sum(r => r.Amount)
                    : accounted.Where(r => r.Resource == resource && r.Artifact is not null && Key(r.Artifact) == Key(artifact)).Sum(r => r.Amount);
                if (amount <= used) return;
                accounted.Add(new("observed:" + vm.Name + ":" + resource + ":" + artifact, resource, vm.Owner, vm.Name,
                    artifact, volume, amount - used, ReservationPhase.Held, ReservationOrigin.External, null, inventory.ObservedAt, null, inventory.ObservedAt));
            }
            if (!ReservationRules.Terminal(actual.State))
            { Hold(ReservationResource.Ram, Math.Max(vm.RamBytes, Memory(actual))); Hold(ReservationResource.Cpu, Math.Max(vm.Cpu, actual.Cpus)); }
            foreach (var disk in actual.Disks) Hold(ReservationResource.Storage, disk.MaxBytes, "disk:" + disk.Path, disk.Volume);
            if (actual.State != VmState.Off && actual.State != VmState.Absent)
                Hold(ReservationResource.Storage, checked(Math.Max(vm.RamBytes, Memory(actual)) + SavedStateOverhead), "saved-state:" + actual.Id, actual.ConfigVolume);
        }
        return accounted;
    }

    public static HostCapacitySnapshot Calculate(InventorySnapshot inventory, CapacityConfig config,
        IReadOnlyList<Reservation> reservations, IReadOnlyList<Vm> managed)
    {
        var persisted = reservations;
        reservations = AccountedReservations(inventory, reservations, managed);
        var problems = inventory.Problems.ToList();
        var complete = inventory.Complete && inventory.Vms.All(v => v.Complete && v.Disks.All(d => d.Readable));
        if (!complete && problems.Count == 0) problems.Add("inventory-incomplete");
        var unmanaged = inventory.Vms.Where(a => !managed.Any(m => Matches(m, a))).ToArray();
        var runtime = reservations.Where(r => r.Resource == ReservationResource.Ram).ToArray();
        var ram = runtime.Sum(r => r.Amount);
        var unreflected = runtime.GroupBy(r => r.VmName, StringComparer.OrdinalIgnoreCase).Sum(group =>
        {
            var vm = managed.FirstOrDefault(v => Ownership.SameName(v.Name, group.Key));
            var actual = vm is null ? null : inventory.Vms.FirstOrDefault(a => Matches(vm, a));
            var assigned = actual is { Complete: true } && !ReservationRules.Terminal(actual.State) ? Math.Max(0, actual.MemoryAssignedBytes) : 0;
            return Math.Max(0, group.Sum(r => r.Amount) - assigned);
        });
        var externalRam = unmanaged.Where(v => !ReservationRules.Terminal(v.State)).Sum(Memory);
        var headroom = config.RamHeadroomBytes ?? Math.Max(4L << 30, inventory.Host.TotalRamBytes / 8);
        var available = Math.Max(0, Math.Min(inventory.Host.TotalRamBytes - headroom - ram - externalRam,
            inventory.Host.FreeRamBytes - headroom - unreflected));
        long cpus = reservations.Where(r => r.Resource == ReservationResource.Cpu).Sum(r => r.Amount)
            + unmanaged.Where(v => !ReservationRules.Terminal(v.State)).Sum(v => (long)v.Cpus);
        var artifacts = new Dictionary<string, (string Volume, long Maximum, long Allocated)>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, string volume, long maximum, long allocated)
        {
            key = Key(key);
            if (artifacts.TryGetValue(key, out var old)) artifacts[key] = (old.Volume, Math.Max(old.Maximum, maximum), Math.Max(old.Allocated, allocated));
            else artifacts[key] = (volume, maximum, allocated);
        }
        foreach (var actual in inventory.Vms)
        {
            foreach (var disk in actual.Disks) Add("path:" + Key(disk.Path), disk.Volume, disk.MaxBytes, disk.Readable ? disk.FileBytes : 0);
            if (actual.State != VmState.Off && actual.State != VmState.Absent)
                Add("saved-state:" + actual.Id, actual.ConfigVolume, checked(Memory(actual) + SavedStateOverhead), actual.SavedStateBytes ?? 0);
        }
        var storage = reservations.Where(r => r.Resource == ReservationResource.Storage).Select(row =>
        {
            var evidence = inventory.Artifacts?.FirstOrDefault(a => Key(a.Artifact) == Key(row.Artifact!));
            var diskPath = row.Artifact?.StartsWith("disk:", StringComparison.OrdinalIgnoreCase) == true ? row.Artifact[5..] : null;
            var key = ReservationRules.SavedState(row) ? row.Artifact! : evidence?.Path is { } path ? "path:" + Key(path) : diskPath is not null ? "path:" + Key(diskPath) : row.Artifact ?? row.Id;
            var observedDisk = diskPath is null ? null : inventory.Vms.SelectMany(v => v.Disks).FirstOrDefault(d => Key(d.Path) == Key(diskPath));
            var observedSaved = ReservationRules.SavedState(row) ? inventory.Vms.FirstOrDefault(v => Key("saved-state:" + v.Id) == Key(row.Artifact!)) : null;
            // Running/paused/transient VMs reserve future saved-state growth; no VMRS is required yet.
            // Off VMs may retain a stale hold until confirmed-stop reconciliation releases it.
            var savedGrowthOnly = observedSaved?.State is VmState.Running or VmState.Paused or VmState.Unknown or VmState.Off;
            var knownPresent = evidence?.Presence == ArtifactPresence.Present || observedDisk?.Readable == true || observedSaved?.SavedStateBytes > 0 || savedGrowthOnly;
            if (evidence?.Presence == ArtifactPresence.Unknown || observedSaved?.State == VmState.Saved && !knownPresent ||
                row.Phase == ReservationPhase.Held && !knownPresent && !row.Id.StartsWith("observed:", StringComparison.Ordinal))
            { complete = false; problems.Add("artifact-unreadable-or-missing"); }
            return (Row: row, Key: Key(key), Allocated: evidence?.Presence == ArtifactPresence.Present ? evidence.FileBytes : 0);
        }).ToArray();
        // Reconciliation can add a delta after an external disk resize. Those liabilities are additive,
        // even when there is no readable disk left to supply its new maximum on the next pass.
        foreach (var group in storage.GroupBy(s => s.Key))
            Add(group.Key, group.First().Row.Volume!, group.Sum(s => s.Row.Amount), group.Max(s => s.Allocated));
        var volumes = inventory.Host.Volumes.Select(v =>
        {
            var growth = artifacts.Values.Where(a => Key(a.Volume) == Key(v.Root)).Sum(a => Math.Max(0, a.Maximum - a.Allocated));
            return new VolumeCapacity(v.Root, v.TotalBytes, v.FreeBytes, config.StorageHeadroomBytes, growth,
                Math.Max(0, v.FreeBytes - config.StorageHeadroomBytes - growth));
        }).ToArray();
        if (artifacts.Values.Any(a => !volumes.Any(v => Key(v.Root) == Key(a.Volume))))
        { complete = false; problems.Add("volume-unavailable"); }
        return new(inventory.Epoch, inventory.ObservedAt, complete, inventory.Host.TotalRamBytes, headroom, ram, externalRam,
            inventory.Host.FreeRamBytes, available, inventory.Host.LogicalCpus, config.CpuBudget, (int)Math.Min(int.MaxValue, cpus),
            config.CpuBudget is int budget ? (int)Math.Max(0, budget - cpus) : null, volumes, persisted.ToArray(), unmanaged, problems.Distinct().ToArray());
    }

    public static CapacityDecision Decide(ReservationRequest request, HostCapacitySnapshot snapshot, CapacityConfig config,
        EffectiveAllowance? allowance, IReadOnlyList<Reservation>? accounting = null)
    {
        CapacityDecision? refusal = null;
        void Check(string resource, string scope, long requested, long allowed, long available, string reason)
        { if (refusal is null && requested > available) refusal = new(false, [], resource, scope, requested, allowed, Math.Max(0, available), reason, snapshot.Epoch); }
        foreach (var resource in new[] { ReservationResource.Cpu, ReservationResource.Ram, ReservationResource.Storage })
        {
            var requested = request.Lines.Where(l => l.Resource == resource).Sum(l => l.Amount);
            var used = (accounting ?? snapshot.Reservations).Where(r => r.Resource == resource && Ownership.SameName(r.ScopeOwner, request.Owner)).Sum(r => r.Amount);
            long? budget = resource switch { ReservationResource.Cpu => allowance?.CpuBudget, ReservationResource.Ram => allowance?.RamBudgetBytes, _ => allowance?.StorageBudgetBytes };
            if (budget is long max) Check(resource.ToString().ToLowerInvariant(), "user", requested, max, Math.Max(0, max - used), "user-budget");
        }
        if (!snapshot.Complete) refusal ??= new(false, [], null, "host", 0, 0, 0, "inventory-incomplete", snapshot.Epoch);
        var cpu = request.Lines.Where(l => l.Resource == ReservationResource.Cpu).Sum(l => l.Amount);
        if (config.MaxVcpusPerVm is int maxCpu && request.VmName is not null) Check("cpu", "host", cpu, maxCpu, maxCpu, "per-vm-maximum");
        if (snapshot.CpuAvailable is int cpuAvailable) Check("cpu", "host", cpu, snapshot.CpuBudget!.Value, cpuAvailable, "host-budget");
        Check("ram", "host", request.Lines.Where(l => l.Resource == ReservationResource.Ram).Sum(l => l.Amount),
            Math.Max(0, snapshot.RamTotalBytes - snapshot.RamHeadroomBytes), snapshot.RamAvailableBytes, "host-capacity");
        foreach (var group in request.Lines.Where(l => l.Resource == ReservationResource.Storage).GroupBy(l => Key(l.Volume!)))
        {
            var volume = snapshot.Volumes.FirstOrDefault(v => Key(v.Root) == group.Key);
            Check("storage", "volume", group.Sum(l => l.Amount), Math.Max(0, (volume?.TotalBytes ?? 0) - (volume?.HeadroomBytes ?? 0)), volume?.AvailableBytes ?? 0, "volume-capacity");
        }
        if (refusal is null) return new(true, [], null, null, 0, 0, 0, null, snapshot.Epoch);
        return config.Mode == CapacityMode.Observe ? refusal with { Allowed = true, Reason = "observe:" + refusal.Reason } : refusal;
    }
}
