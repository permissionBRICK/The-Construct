using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Fakes;

public sealed partial class InMemoryVmRepository
{
    private readonly Dictionary<string, VmOverride> _overrides = new(Ownership.NameComparer);
    private readonly Dictionary<string, CascadePreview> _cascades = new(Ownership.NameComparer);
    public Task<IReadOnlyList<Vm>> ListChildrenAsync(string parent, CancellationToken ct) => Task.FromResult<IReadOnlyList<Vm>>(
        _vms.Values.Where(v => Ownership.SameName(v.Parent, parent)).OrderBy(v => v.Name, Ownership.NameComparer).ToArray());
    public Task<IReadOnlyList<Vm>> ListSharedAsync(SharingScope scope, CancellationToken ct) => Task.FromResult<IReadOnlyList<Vm>>(
        _vms.Values.Where(v => v.Kind == VmKind.Child && v.Sharing == scope).ToArray());
    public Task<int> CountByOwnerAsync(string owner, VmKind kind, CancellationToken ct) => Task.FromResult(
        _vms.Values.Count(v => Ownership.SameName(v.Owner, owner) && v.Kind == kind));
    public Task<VmAddDecision> AddAsync(Vm vm, EffectiveAllowance allowance, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_writeGate)
        {
            if (_vms.ContainsKey(vm.Name)) return Task.FromResult(VmAddDecision.NameTaken);
            if (vm.Kind == VmKind.Child)
            {
                if (vm.Parent is null || !_vms.TryGetValue(vm.Parent, out var parent) || parent.Kind != VmKind.Primary || !Ownership.SameName(parent.Owner, vm.Owner))
                    return Task.FromResult(VmAddDecision.ParentMissing);
                if (parent.Deleting || parent.ChildCreationClosed) return Task.FromResult(VmAddDecision.ParentClosed);
                if (vm.Lease is null || vm.Hardware is null || vm.RamMb is null || vm.VmTokenHash is not null)
                    throw new ArgumentException("A child requires hardware, RAM and lease, and cannot hold a credential.");
            }
            else if (vm.Parent is not null) throw new ArgumentException("A primary cannot have a parent.");
            if (_vms.Values.Count(v => Ownership.SameName(v.Owner, vm.Owner) && v.Kind == vm.Kind) >=
                (vm.Kind == VmKind.Primary ? allowance.MaxPrimaries : allowance.MaxRetainedChildren))
                return Task.FromResult(vm.Kind == VmKind.Primary ? VmAddDecision.PrimaryQuotaExceeded : VmAddDecision.ChildrenQuotaExceeded);
            _vms[vm.Name] = vm; return Task.FromResult(VmAddDecision.Added);
        }
    }
    public Task<bool> TryFenceAsync(string name, string jobId, bool closeChildCreation, CancellationToken ct)
    {
        lock (_writeGate)
        {
            if (!_vms.TryGetValue(name, out var vm) || HasLiveJob(vm)) return Task.FromResult(false);
            _vms[name] = vm with { Deleting = true, VmTokenHash = null, CurrentJobId = jobId, ChildCreationClosed = closeChildCreation || vm.ChildCreationClosed };
            return Task.FromResult(true);
        }
    }
    public Task<bool> UpdateLeaseAsync(string name, Lease lease, long expectedVersion, CancellationToken ct)
    {
        lock (_writeGate)
        {
            if (!_vms.TryGetValue(name, out var vm) || vm.Kind != VmKind.Child || (vm.Lease?.Version ?? 0) != expectedVersion || lease.Version != expectedVersion + 1) return Task.FromResult(false);
            _vms[name] = vm with { Lease = lease }; return Task.FromResult(true);
        }
    }
    public Task<IReadOnlyList<Vm>> ListLeasesDueAsync(DateTimeOffset now, TimeSpan retryAfter, CancellationToken ct) => Task.FromResult<IReadOnlyList<Vm>>(
        _vms.Values.Where(v => v.Kind == VmKind.Child && !v.Deleting && v.Lease is { State: LeaseState.Active or LeaseState.Overdue, ExpiresAt: not null } l &&
            l.ExpiresAt <= now && (l.LastExpiryAttemptAt is null || l.LastExpiryAttemptAt <= now - retryAfter)).ToArray());
    public Task<VmOverride?> GetOverrideAsync(string vmName, CancellationToken ct)
    { lock (_writeGate) return Task.FromResult(_overrides.GetValueOrDefault(vmName)); }
    public Task SetOverrideAsync(VmOverride value, CancellationToken ct)
    { lock (_writeGate) _overrides[value.VmName] = value; return Task.CompletedTask; }
    public Task<bool> RemoveOverrideAsync(string vmName, CancellationToken ct)
    { lock (_writeGate) return Task.FromResult(_overrides.Remove(vmName)); }
    public Task<CascadePreview> SaveCascadePreviewAsync(CascadePreview preview, CancellationToken ct)
    { lock (_writeGate) _cascades[preview.Parent] = preview; return Task.FromResult(preview); }
    public Task<CascadePreview?> GetCascadePreviewAsync(string parent, CancellationToken ct)
    { lock (_writeGate) return Task.FromResult(_cascades.GetValueOrDefault(parent)); }
    public Task<CascadeAcceptance> TryAcceptCascadeAsync(string parent, string token, string jobId, CancellationToken ct)
    {
        lock (_writeGate)
        {
            var children = _vms.Values.Where(v => Ownership.SameName(v.Parent, parent)).ToArray();
            var current = CascadeRules.Children(children);
            if (children.Any(HasLiveJob) || _vms.TryGetValue(parent, out var parentVm) && HasLiveJob(parentVm))
                return Task.FromResult(new CascadeAcceptance(false,"operation-in-progress",current,null));
            if (!_vms.TryGetValue(parent, out var vm) || !_cascades.TryGetValue(parent, out var preview) ||
                !CascadeRules.Matches(preview, vm, current, token, (clock?.UtcNow ?? DateTimeOffset.UtcNow)))
                return Task.FromResult(new CascadeAcceptance(false, "cascade-mismatch", current, null));
            foreach (var child in children) _vms[child.Name] = child with { Deleting = true, VmTokenHash = null, CurrentJobId = jobId };
            _vms[parent] = vm with { Deleting = true, ChildCreationClosed = true, VmTokenHash = null, CurrentJobId = jobId };
            _cascades[parent] = preview with { State = CascadeState.Accepted, JobId = jobId };
            return Task.FromResult(new CascadeAcceptance(true, null, current, null));
        }
    }
    public Task<bool> UpdateGuestReportAsync(string name, GuestReport report, CancellationToken ct)
    {
        lock (_writeGate)
        {
            if (!_vms.TryGetValue(name, out var vm) || vm.Kind != VmKind.Primary || vm.Deleting) return Task.FromResult(false);
            _vms[name] = vm with { Guest = GuestReportRules.Merge(vm.Guest, report) }; return Task.FromResult(true);
        }
    }
    public Task<bool> UpdateObservationAsync(string name, HostObservation observation, CancellationToken ct)
    {
        lock (_writeGate)
        {
            if (!_vms.TryGetValue(name, out var vm)) return Task.FromResult(false);
            _vms[name] = vm with { Observed = observation }; return Task.FromResult(true);
        }
    }
    public Task<bool> SetTokenAsync(string name, string? hash, VmTokenKind kind, CancellationToken ct)
    {
        lock (_writeGate)
        {
            if (!_vms.TryGetValue(name, out var vm) || vm.Kind != VmKind.Primary || vm.Deleting) return Task.FromResult(false);
            _vms[name] = vm with { VmTokenHash = hash, TokenKind = kind }; return Task.FromResult(true);
        }
    }
    // In-memory job-store tasks complete synchronously. Tests with no store conservatively
    // treat a named operation as live; DI supplies the shared job store.
    private bool HasLiveJob(Vm vm) => vm.CurrentJobId is not null && (jobs is null ||
        jobs.GetAsync(vm.CurrentJobId, CancellationToken.None).GetAwaiter().GetResult() is { State: JobState.Queued or JobState.Running });
    public Task<bool> UpdateIncarnationAsync(string name,string incarnation,CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incarnation);
        lock(_writeGate)
        {
            if(!_vms.TryGetValue(name,out var vm) || vm.Incarnation is not null && vm.Incarnation!=incarnation)return Task.FromResult(false);
            _vms[name]=vm with {Incarnation=incarnation};return Task.FromResult(true);
        }
    }
}
