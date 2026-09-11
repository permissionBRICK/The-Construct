using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Api.Jobs;

/// <summary>Callers hold the VM gate for desired writes and Off-only application.</summary>
public sealed class PrimaryMemorySettings(IHostConfigStore config, ICapacityLedger capacity,
    IDelegationPolicy policy, IVmRepository vms, IVmMemoryDriver driver, IAdmissionStore admission)
{
    public sealed record Setting(DateTimeOffset Created, int RamGb);
    public sealed record Limits(int MaximumRamGb, int RecommendedRamGb);
    private static string Section(Vm vm) => "primary-memory:" + vm.Name.ToLowerInvariant();
    public async Task<Limits> LimitsAsync(string owner, Vm? vm, CancellationToken ct)
    {
        const long gib = 1L << 30;
        var snapshot = await capacity.SnapshotAsync(true, ct);
        if (!snapshot.Complete) throw new LifecycleException("capacity-unavailable");
        var allowance = await policy.ResolveAsync(owner, null, ct);
        var rows = (snapshot.Accounting ?? snapshot.Reservations).Where(r => r.Resource == ReservationResource.Ram).ToArray();
        var own = rows.Where(r => vm is not null && Ownership.SameName(r.VmName, vm.Name)).Sum(r => r.Amount);
        // Legacy/precomputed fake snapshots lack reclaim data; production computes it from one epoch.
        var available = vm is not null && snapshot.RamAvailableByVm?.TryGetValue(vm.Name, out var reclaimable) == true
            ? reclaimable : Math.Min(Math.Max(0, snapshot.RamTotalBytes - snapshot.RamHeadroomBytes), snapshot.RamAvailableBytes + own);
        if (allowance.RamBudgetBytes is long budget)
            available = Math.Min(available, Math.Max(0, budget - rows.Where(r => Ownership.SameName(r.ScopeOwner, owner)).Sum(r => r.Amount) + own));
        var maximum = (int)Math.Clamp(available / gib, 0, 1024);
        return new(maximum, Math.Min(8, maximum));
    }
    public async Task<Setting?> GetAsync(Vm vm, CancellationToken ct)
    {
        var setting = await config.GetAsync<Setting>(Section(vm), ct);
        return setting?.Created == vm.Created ? setting : null;
    }
    public Task SaveAsync(Vm vm, int ramGb, string actor, CancellationToken ct) =>
        config.SetAsync(Section(vm), new Setting(vm.Created, ramGb), actor, ct);
    public async Task<Vm> ApplyAsync(Vm vm, VmState state, CancellationToken ct)
    {
        if (vm.Kind != VmKind.Primary || state != VmState.Off) return vm;
        var setting = await GetAsync(vm, ct);
        if (setting is null || setting.RamGb == vm.RamGb) return vm;
        var limits = await LimitsAsync(vm.Owner, vm, ct);
        if (setting.RamGb > limits.MaximumRamGb) throw new LifecycleException("memoryAllowanceExceeded");
        await driver.SetMemoryAsync(vm.Name, setting.RamGb, ct);
        var result = await admission.MutateAsync(null, scope => scope.UpdatePrimaryRamAsync(vm.Name, setting.RamGb, vm.PowerGeneration), ct);
        if (result.Outcome != AdmissionOutcome.Accepted) throw new LifecycleException("power-state-changed");
        return await vms.GetAsync(vm.Name, ct) ?? throw new LifecycleException("vm-deleting");
    }
}
