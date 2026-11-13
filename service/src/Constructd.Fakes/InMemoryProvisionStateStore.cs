using System.Collections.Concurrent;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Fakes;

/// <summary>In-memory desired and applied provisioning state per VM.</summary>
public sealed class InMemoryProvisionStateStore : IVmDesiredStateSource, IVmAppliedStateStore
{
    private readonly ConcurrentDictionary<string, StoredDesiredState> _desired = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, VmAppliedState> _applied = new(StringComparer.OrdinalIgnoreCase);

    public Task<StoredDesiredState?> GetDesiredAsync(string vmName, CancellationToken ct) =>
        Task.FromResult(_desired.TryGetValue(vmName, out var value) ? value : null);

    public Task SetDesiredAsync(string vmName, StoredDesiredState desired, CancellationToken ct)
    {
        _desired[vmName] = desired;
        return Task.CompletedTask;
    }

    public Task<VmAppliedState?> GetAppliedAsync(string vmName, CancellationToken ct) =>
        Task.FromResult(_applied.TryGetValue(vmName, out var value) ? value : null);

    public Task SetAppliedAsync(string vmName, VmAppliedState applied, CancellationToken ct)
    {
        _applied[vmName] = applied;
        return Task.CompletedTask;
    }
}
