using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Core.Services;

/// <summary>
/// The VM registry with its changes on the <see cref="ChangeFeed"/>: <c>vm.created</c>,
/// <c>vm.changed</c>, <c>vm.power</c> (the state changed) and <c>vm.deleted</c>. Every other call
/// passes through. Guest reports, host observations and leases are not announced: they arrive with
/// every heartbeat and reconcile pass, and a client re-reads them with the VM.
/// </summary>
public sealed class ChangeReportingVmRepository(IVmRepository inner, ChangeFeed changes) : IVmRepository, IVmDelegationRepository, IVmMetadataStore
{
    private IVmDelegationRepository Delegation => (IVmDelegationRepository)inner;

    private IVmMetadataStore Metadata => (IVmMetadataStore)inner;

    public Task<Vm?> GetAsync(string name, CancellationToken cancellationToken) => inner.GetAsync(name, cancellationToken);

    public Task<IReadOnlyList<Vm>> ListAsync(string? owner, CancellationToken cancellationToken) => inner.ListAsync(owner, cancellationToken);

    public async Task<VmAddOutcome> AddAsync(Vm vm, int maxVms, CancellationToken cancellationToken)
    {
        var outcome = await inner.AddAsync(vm, maxVms, cancellationToken).ConfigureAwait(false);
        if (outcome == VmAddOutcome.Added) changes.Vm("vm.created", vm);
        return outcome;
    }

    public async Task<bool> UpdateAsync(Vm vm, CancellationToken cancellationToken)
    {
        var before = await inner.GetAsync(vm.Name, cancellationToken).ConfigureAwait(false);
        var updated = await inner.UpdateAsync(vm, cancellationToken).ConfigureAwait(false);
        if (!updated) return false;
        changes.Vm(before is not null && before.State != vm.State ? "vm.power" : "vm.changed", vm);
        // A VM given to another user: the old owner hears of it too, and finds it gone.
        if (before is not null && !Logic.Ownership.SameName(before.Owner, vm.Owner)) changes.Vm("vm.changed", before);
        return updated;
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken cancellationToken)
    {
        var before = await inner.GetAsync(name, cancellationToken).ConfigureAwait(false);
        var removed = await inner.RemoveAsync(name, cancellationToken).ConfigureAwait(false);
        if (removed && before is not null) changes.Vm("vm.deleted", before);
        return removed;
    }

    public Task<int> CountByOwnerAsync(string owner, CancellationToken cancellationToken) => inner.CountByOwnerAsync(owner, cancellationToken);

    public Task SaveActivityAsync(ActivityReport report, CancellationToken cancellationToken) => inner.SaveActivityAsync(report, cancellationToken);

    public Task<ActivityReport?> GetLatestActivityAsync(string vmName, CancellationToken cancellationToken) => inner.GetLatestActivityAsync(vmName, cancellationToken);

    // ---- IVmDelegationRepository ----

    public Task<IReadOnlyList<Vm>> ListChildrenAsync(string parent, CancellationToken ct) => Delegation.ListChildrenAsync(parent, ct);

    public Task<IReadOnlyList<Vm>> ListSharedAsync(SharingScope scope, CancellationToken ct) => Delegation.ListSharedAsync(scope, ct);

    public Task<int> CountByOwnerAsync(string owner, VmKind kind, CancellationToken ct) => Delegation.CountByOwnerAsync(owner, kind, ct);

    public async Task<VmAddDecision> AddAsync(Vm vm, EffectiveAllowance allowance, CancellationToken ct)
    {
        var decision = await Delegation.AddAsync(vm, allowance, ct).ConfigureAwait(false);
        if (decision == VmAddDecision.Added) changes.Vm("vm.created", vm);
        return decision;
    }

    public async Task<bool> TryFenceAsync(string name, string jobId, bool closeChildCreation, CancellationToken ct)
    {
        var fenced = await Delegation.TryFenceAsync(name, jobId, closeChildCreation, ct).ConfigureAwait(false);
        if (fenced) await ChangedAsync(name, ct).ConfigureAwait(false);
        return fenced;
    }

    public Task<bool> UpdateLeaseAsync(string name, Lease lease, long expectedVersion, CancellationToken ct) => Delegation.UpdateLeaseAsync(name, lease, expectedVersion, ct);

    public Task<IReadOnlyList<Vm>> ListLeasesDueAsync(DateTimeOffset now, TimeSpan retryAfter, CancellationToken ct) => Delegation.ListLeasesDueAsync(now, retryAfter, ct);

    public Task<VmOverride?> GetOverrideAsync(string vmName, CancellationToken ct) => Delegation.GetOverrideAsync(vmName, ct);

    public Task SetOverrideAsync(VmOverride value, CancellationToken ct) => Delegation.SetOverrideAsync(value, ct);

    public Task<bool> RemoveOverrideAsync(string vmName, CancellationToken ct) => Delegation.RemoveOverrideAsync(vmName, ct);

    public Task<CascadePreview> SaveCascadePreviewAsync(CascadePreview preview, CancellationToken ct) => Delegation.SaveCascadePreviewAsync(preview, ct);

    public Task<CascadePreview?> GetCascadePreviewAsync(string parent, CancellationToken ct) => Delegation.GetCascadePreviewAsync(parent, ct);

    public Task<CascadeAcceptance> TryAcceptCascadeAsync(string parent, string token, string jobId, bool keepShared, CancellationToken ct) =>
        Delegation.TryAcceptCascadeAsync(parent, token, jobId, keepShared, ct);

    public Task<bool> UpdateGuestReportAsync(string name, GuestReport report, CancellationToken ct) => Delegation.UpdateGuestReportAsync(name, report, ct);

    public Task<bool> UpdateObservationAsync(string name, HostObservation observation, CancellationToken ct) => Delegation.UpdateObservationAsync(name, observation, ct);

    // ---- IVmMetadataStore ----

    public async Task<bool> RecordPressureSaveAsync(string name, long expectedGeneration, DateTimeOffset at, CancellationToken ct)
    {
        var recorded = await Metadata.RecordPressureSaveAsync(name, expectedGeneration, at, ct).ConfigureAwait(false);
        if (recorded) await ChangedAsync(name, ct).ConfigureAwait(false);
        return recorded;
    }

    public async Task<bool> RestoreUnqueuedDeletionAsync(Vm original, CancellationToken ct)
    {
        var restored = await Metadata.RestoreUnqueuedDeletionAsync(original, ct).ConfigureAwait(false);
        if (restored) changes.Vm("vm.changed", original);
        return restored;
    }

    public Task<bool> UpdateIncarnationAsync(string name, string incarnation, CancellationToken ct) => Metadata.UpdateIncarnationAsync(name, incarnation, ct);

    public Task<bool> SetTokenAsync(string name, string? hash, VmTokenKind kind, CancellationToken ct) => Metadata.SetTokenAsync(name, hash, kind, ct);

    private async Task ChangedAsync(string name, CancellationToken ct)
    {
        if (await inner.GetAsync(name, ct).ConfigureAwait(false) is { } vm) changes.Vm("vm.changed", vm);
    }
}
