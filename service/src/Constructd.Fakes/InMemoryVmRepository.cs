using System.Collections.Concurrent;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Fakes;

/// <summary>In-memory VM registry plus the latest activity heartbeat per VM.</summary>
public sealed partial class InMemoryVmRepository(IJobStore? jobs = null, IClock? clock = null) : IVmRepository, IVmDelegationRepository, IVmMetadataStore
{
    private readonly ConcurrentDictionary<string, Vm> _vms = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ActivityReport> _activity = new(StringComparer.OrdinalIgnoreCase);

    public Task<Vm?> GetAsync(string name, CancellationToken cancellationToken)
    {
        lock (InMemoryTransaction.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_vms.TryGetValue(name, out var vm) ? vm : null);

        }
    }

    public Task<IReadOnlyList<Vm>> ListAsync(string? owner, CancellationToken cancellationToken)
    {
        lock (InMemoryTransaction.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<Vm> vms = _vms.Values
                .Where(vm => owner is null || Ownership.SameName(vm.Owner, owner))
                .OrderBy(vm => vm.Name, StringComparer.Ordinal)
                .ToList();

            return Task.FromResult(vms);

        }
    }

    public Task<VmAddOutcome> AddAsync(Vm vm, int maxVms, CancellationToken cancellationToken)
    {
        lock (InMemoryTransaction.Gate)
        {
            ArgumentNullException.ThrowIfNull(vm);
            cancellationToken.ThrowIfCancellationRequested();

            // Name check, quota check and insert under one lock: concurrent creates must not both pass.
            {
                if (_vms.ContainsKey(vm.Name))
                {
                    return Task.FromResult(VmAddOutcome.NameTaken);
                }

                if (_vms.Values.Count(existing => Ownership.SameName(existing.Owner, vm.Owner) && existing.Kind == VmKind.Primary) >= maxVms)
                {
                    return Task.FromResult(VmAddOutcome.QuotaExceeded);
                }

                _vms[vm.Name] = vm;
                return Task.FromResult(VmAddOutcome.Added);
            }

        }
    }

    public Task<bool> UpdateAsync(Vm vm, CancellationToken cancellationToken)
    {
        lock (InMemoryTransaction.Gate)
        {
            ArgumentNullException.ThrowIfNull(vm);
            cancellationToken.ThrowIfCancellationRequested();

            {
                if (!_vms.TryGetValue(vm.Name, out var old)) return Task.FromResult(false);
                _vms[vm.Name] = old with
                {
                    Owner = vm.Owner,
                    Cpu = vm.Cpu,
                    RamGb = vm.RamGb,
                    DiskGb = vm.DiskGb,
                    State = vm.State,
                    SshForwardPort = vm.SshForwardPort,
                    VmTokenHash = vm.Deleting ? null : old.VmTokenHash,
                    IdlePolicy = vm.IdlePolicy,
                    Deleting = vm.Deleting || old.Deleting
                };
                return Task.FromResult(true);
            }

        }
    }

    public Task<bool> RemoveAsync(string name, CancellationToken cancellationToken)
    {
        lock (InMemoryTransaction.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _activity.TryRemove(name, out _);
            return Task.FromResult(_vms.TryRemove(name, out _));

        }
    }

    public Task<int> CountByOwnerAsync(string owner, CancellationToken cancellationToken)
    {
        lock (InMemoryTransaction.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_vms.Values.Count(vm => Ownership.SameName(vm.Owner, owner)));

        }
    }

    public Task SaveActivityAsync(ActivityReport report, CancellationToken cancellationToken)
    {
        lock (InMemoryTransaction.Gate)
        {
            ArgumentNullException.ThrowIfNull(report);
            cancellationToken.ThrowIfCancellationRequested();
            _activity[report.VmName] = report;
            return Task.CompletedTask;

        }
    }

    public Task<ActivityReport?> GetLatestActivityAsync(string vmName, CancellationToken cancellationToken)
    {
        lock (InMemoryTransaction.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_activity.TryGetValue(vmName, out var report) ? report : null);

        }
    }
}
