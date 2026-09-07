using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Fakes;

/// <summary>One transaction across the same in-memory stores used by readers and job workers.</summary>
public sealed class InMemoryAdmissionStore(InMemoryVmRepository vms, InMemoryUserStore users,
    InMemoryMediaStore media, InMemoryCapacityLedger capacity, InMemoryOperationKeyStore keys,
    InMemoryJobStore jobs, InMemoryAuditLog audit, IClock clock) : IAdmissionStore
{
    private static T Done<T>(Task<T> task)
    {
        if (!task.IsCompleted) throw new InvalidOperationException("Admission scopes permit only synchronous in-memory store operations.");
        return task.GetAwaiter().GetResult();
    }
    private static AdmissionResult Result(AdmissionOutcome outcome, OperationKeyRecord? key = null,
        CapacityDecision? capacity = null, CascadeAcceptance? cascade = null, IReadOnlyList<string>? ids = null) => new(outcome, key, capacity, cascade, ids ?? []);
    private AdmissionResult? InsertKey(OperationKeyRecord? key, CancellationToken ct)
    {
        if (key is null) return null;
        var prior = Done(keys.GetAsync(key.Owner, key.Kind, key.Key, ct));
        if (prior is not null) return Result(prior.Fingerprint == key.Fingerprint && prior.Target == key.Target ? AdmissionOutcome.Replay : AdmissionOutcome.KeyConflict, prior);
        Done(keys.TryInsertAsync(key, ct)); return null;
    }
    private AdmissionResult Transaction(Func<AdmissionResult> work, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (InMemoryTransaction.Gate)
        {
            var rollback = new[] { vms.SnapshotForRollback(), users.SnapshotForRollback(), media.SnapshotForRollback(),
                capacity.SnapshotForRollback(), keys.SnapshotForRollback(), jobs.SnapshotForRollback(), audit.SnapshotForRollback() };
            var accepted = false;
            try { var result = work(); ct.ThrowIfCancellationRequested(); accepted = result.Outcome == AdmissionOutcome.Accepted; return result; }
            finally { if (!accepted) foreach (var restore in rollback) restore(); }
        }
    }
    public Task<AdmissionResult> AdmitAsync(AdmissionPlan plan, CancellationToken ct) => Task.FromResult(Transaction(() =>
    {
        if (InsertKey(plan.OperationKey, ct) is { } replay) return replay;
        if (plan.VmToInsert is { } vm)
        {
            if (plan.Allowance is null) throw new ArgumentException("VM admission requires an effective allowance.");
            var added = Done(vms.AddAsync(vm, plan.Allowance, ct));
            if (added != VmAddDecision.Added) return Result(added switch
            {
                VmAddDecision.NameTaken => AdmissionOutcome.NameTaken,
                VmAddDecision.ParentClosed => AdmissionOutcome.ParentClosed,
                VmAddDecision.ParentMissing => AdmissionOutcome.ParentMissing,
                _ => AdmissionOutcome.QuotaExceeded
            });
        }
        foreach (var item in plan.MediaToInsert) media.AddAsync(item, ct).GetAwaiter().GetResult();
        foreach (var upload in plan.UploadsToInsert) media.AddUploadAsync(upload, ct).GetAwaiter().GetResult();
        foreach (var reference in plan.ReferencesToInsert)
            if (!Done(media.TryAddReferenceAsync(reference, ct))) return Result(AdmissionOutcome.MediaNotReady);
        CapacityDecision? decision = null;
        if (plan.Reservation is { } request)
        { decision = Done(capacity.TryReserveAsync(request, ct)); if (!decision.Allowed) return Result(AdmissionOutcome.CapacityRefused, capacity: decision); }
        CascadeAcceptance? cascade = null;
        if (plan.CascadeToAccept is { } preview)
        {
            if (plan.FenceJobId is null) throw new ArgumentException("Cascade admission requires a fence job.");
            cascade = Done(vms.TryAcceptCascadeAsync(preview.Parent, preview.Token, plan.FenceJobId, ct));
            if (!cascade.Accepted) return Result(AdmissionOutcome.CascadeMismatch, cascade: cascade);
        }
        else if (plan.VmToFence is { } name && (plan.FenceJobId is null || !Done(vms.TryFenceAsync(name, plan.FenceJobId, plan.CloseChildCreation, ct))))
            return Result(AdmissionOutcome.VersionConflict);
        if (plan.JobToInsert is { } job)
        {
            if (job.State != JobState.Queued || Done(jobs.GetAsync(job.Id, ct)) is not null) return Result(AdmissionOutcome.VersionConflict);
            jobs.UpsertAsync(job, ct).GetAwaiter().GetResult();
        }
        return Result(AdmissionOutcome.Accepted, capacity: decision, cascade: cascade, ids: decision?.ReservationIds);
    }, ct));
    public Task<AdmissionResult> MutateAsync(OperationKeyRecord? key, Func<IAdmissionScope, Task<bool>> mutation, CancellationToken ct) => Task.FromResult(Transaction(() =>
    {
        // External intents may already be InFlight; completed mutations replay without running the delegate.
        if (key is not null && Done(keys.GetAsync(key.Owner, key.Kind, key.Key, ct)) is { } prior)
        {
            if (prior.Fingerprint != key.Fingerprint || prior.Target != key.Target) return Result(AdmissionOutcome.KeyConflict, prior);
            if (prior.State == OperationKeyState.Completed) return Result(AdmissionOutcome.Replay, prior);
        }
        else if (InsertKey(key, ct) is { } conflict) return conflict;
        var scope = new Scope(vms, users, capacity, keys, audit, ct);
        try { return Result(Done(mutation(scope)) && !scope.Conflict ? AdmissionOutcome.Accepted : AdmissionOutcome.VersionConflict); }
        finally { scope.Active = false; }
    }, ct));
    public Task MarkStartFailedAsync(string jobId, string error, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            var job = Done(jobs.GetAsync(jobId, ct)) ?? throw new KeyNotFoundException("Unknown queued job.");
            // There is no hypervisor absence evidence here. Retain tombstones/fences/liabilities for
            // reconciliation rather than releasing a disk or RAM hold on an assumed rollback.
            if (job.State != JobState.Queued) return Task.CompletedTask;
            return jobs.UpsertAsync(job with { State = JobState.Failed, Error = "Persisted job could not start.", Finished = clock.UtcNow }, ct);
        }
    }
    private sealed class Scope(InMemoryVmRepository vms, InMemoryUserStore users, InMemoryCapacityLedger capacity,
        InMemoryOperationKeyStore keys, InMemoryAuditLog audit, CancellationToken ct) : IAdmissionScope
    {
        internal bool Active = true;
        internal bool Conflict;
        private void Check() { if (!Active || !InMemoryTransaction.Gate.IsHeldByCurrentThread) throw new InvalidOperationException("Admission scope is no longer active."); ct.ThrowIfCancellationRequested(); }
        private Task<bool> Cas(Func<Task<bool>> operation) { Check(); var value = Done(operation()); Conflict |= !value; return Task.FromResult(value); }
        public Task<bool> UpdateLeaseAsync(string vmName, Lease lease, long expectedVersion) => Cas(() => vms.UpdateLeaseAsync(vmName, lease, expectedVersion, ct));
        public Task<bool> UpdateSharingAsync(string vmName, SharingScope scope) => Cas(() => vms.ChangeSharingAsync(vmName, scope));
        public Task SetOverrideAsync(VmOverride value) { Check(); return vms.SetOverrideAsync(value, ct); }
        public Task<bool> SetAllowanceAsync(string userName, UserAllowance allowance) => Cas(() => users.SetAllowanceAsync(userName, allowance, ct));
        public Task<bool> BumpPowerGenerationAsync(string vmName, long expected) => Cas(() => vms.BumpPowerGenerationAsync(vmName, expected));
        public Task<Vm?> ReadVmAsync(string vmName) { Check(); return vms.GetAsync(vmName, ct); }
        public Task<CapacityDecision> ReserveAsync(ReservationRequest request) { Check(); var result = Done(capacity.TryReserveAsync(request, ct)); return Task.FromResult(result); }
        public Task ConfirmReservationsAsync(IReadOnlyList<string> ids, VmState observed) { Check(); return capacity.ConfirmAsync(ids, observed, ct); }
        public Task ReleaseReservationsAsync(IReadOnlyList<string> ids, VmState observed, string reason) { Check(); return capacity.ReleaseAsync(ids, observed, reason, ct); }
        public Task<bool> CompleteOperationKeyAsync(string owner, string kind, string key, string responseJson) => Cas(() => keys.CompleteAsync(owner, kind, key, responseJson, ct));
        public Task AppendAuditAsync(AuditEntry entry) { Check(); return audit.AppendAsync(entry, ct); }
    }
}
