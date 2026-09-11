using Constructd.Api.Endpoints;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Api.Jobs;

/// <summary>Desired CPU settings are durable; callers hold the VM gate for writes and application.</summary>
public sealed class PrimaryCpuSettings(IHostConfigStore config, ICapacityLedger capacity,
    IDelegationPolicy policy, IVmRepository vms, IVmCpuDriver driver, ConstructdOptions options, IAdmissionStore admission)
{
    public sealed record Setting(DateTimeOffset Created, int Cpus);
    public sealed record Limits(int HostLogicalCpus, int MaximumCpus, int RecommendedCpus);
    private static string Section(Vm vm) => "primary-cpu:" + vm.Name.ToLowerInvariant();

    public async Task<Limits> LimitsAsync(string owner, Vm? vm, CancellationToken ct)
    {
        var snapshot = await capacity.SnapshotAsync(true, ct);
        if (!snapshot.Complete || snapshot.CpuLogical < 1) throw new LifecycleException("capacity-unavailable");
        var allowance = await policy.ResolveAsync(owner, null, ct);
        var settings = await HostAdminEndpoints.CapacityConfigAsync(config, options, ct);
        var maximum = Math.Min(64, snapshot.CpuLogical);
        if (settings.MaxVcpusPerVm is int perVm) maximum = Math.Min(maximum, perVm);
        var other = snapshot.Reservations.Where(r => r.Resource == ReservationResource.Cpu &&
            (vm is null || !Ownership.SameName(r.VmName, vm.Name))).ToArray();
        if (allowance.CpuBudget is int budget)
            maximum = (int)Math.Min(maximum, Math.Max(0, budget - other.Where(r => Ownership.SameName(r.ScopeOwner, owner)).Sum(r => r.Amount)));
        if (snapshot.CpuBudget is int hostBudget)
        {
            var own = snapshot.Reservations.Where(r => r.Resource == ReservationResource.Cpu && vm is not null && Ownership.SameName(r.VmName, vm.Name)).Sum(r => r.Amount);
            maximum = (int)Math.Min(maximum, Math.Max(0, hostBudget - snapshot.CpuActive + own));
        }
        return new(snapshot.CpuLogical, maximum, maximum);
    }

    public async Task<Setting?> GetAsync(Vm vm, CancellationToken ct)
    {
        var setting = await config.GetAsync<Setting>(Section(vm), ct);
        return setting?.Created == vm.Created ? setting : null;
    }

    public Task SaveAsync(Vm vm, int cpus, string actor, CancellationToken ct) =>
        config.SetAsync(Section(vm), new Setting(vm.Created, cpus), actor, ct);

    public async Task<Vm> ApplyAsync(Vm vm, VmState state, CancellationToken ct)
    {
        if (vm.Kind != VmKind.Primary || state != VmState.Off) return vm;
        var setting = await GetAsync(vm, ct);
        if (setting is null || setting.Cpus == vm.Cpu) return vm;
        var limits = await LimitsAsync(vm.Owner, vm, ct);
        if (setting.Cpus > limits.MaximumCpus) throw new LifecycleException("cpu-allowance-exceeded");
        await driver.SetCpuCountAsync(vm.Name, setting.Cpus, ct);
        var result = await admission.MutateAsync(null, scope => scope.UpdatePrimaryCpuAsync(vm.Name, setting.Cpus, vm.PowerGeneration), ct);
        if (result.Outcome != AdmissionOutcome.Accepted) throw new LifecycleException("power-state-changed");
        return await vms.GetAsync(vm.Name, ct) ?? throw new LifecycleException("vm-deleting");
    }
}
