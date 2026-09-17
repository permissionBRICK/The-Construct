using Constructd.Core.Domain;
namespace Constructd.Fakes;

public sealed partial class InMemoryVmRepository
{
    public Task<bool> RecordPressureSaveAsync(string name, long expectedGeneration, DateTimeOffset at, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (InMemoryTransaction.Gate)
        {
            if (!_vms.TryGetValue(name, out var vm) || vm.Deleting || vm.PowerGeneration != expectedGeneration)
                return Task.FromResult(false);
            _vms[name] = vm with { State = VmState.Saved, PowerGeneration = expectedGeneration + 1,
                PressureSavedAt = at, PressureSavedGeneration = expectedGeneration + 1 };
            return Task.FromResult(true);
        }
    }
}
