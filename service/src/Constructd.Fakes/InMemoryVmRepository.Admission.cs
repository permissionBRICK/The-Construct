using Constructd.Core.Domain;
namespace Constructd.Fakes;

public sealed partial class InMemoryVmRepository
{
    internal Task<bool> UpdateHardwareAsync(string name, ChildHardware hardware, long expected)
    {
        lock (InMemoryTransaction.Gate)
        {
            if (!_vms.TryGetValue(name, out var vm) || vm.Kind != VmKind.Child || vm.Deleting || vm.PowerGeneration != expected) return Task.FromResult(false);
            _vms[name] = vm with { Hardware = hardware, Cpu = hardware.Cpus, RamMb = hardware.RamMb,
                DiskGb = hardware.DiskGb, PowerGeneration = checked(expected + 1) }; return Task.FromResult(true);
        }
    }
    internal Task<bool> ChangeSharingAsync(string name, SharingScope scope)
    {
        lock (InMemoryTransaction.Gate)
        {
            if (!_vms.TryGetValue(name, out var vm) || vm.Kind != VmKind.Child || vm.Deleting) return Task.FromResult(false);
            _vms[name] = vm with { Sharing = scope }; return Task.FromResult(true);
        }
    }
    internal Task<bool> BumpPowerGenerationAsync(string name, long expected)
    {
        lock (InMemoryTransaction.Gate)
        {
            if (!_vms.TryGetValue(name, out var vm) || vm.PowerGeneration != expected) return Task.FromResult(false);
            _vms[name] = vm with { PowerGeneration = checked(expected + 1) }; return Task.FromResult(true);
        }
    }
}
