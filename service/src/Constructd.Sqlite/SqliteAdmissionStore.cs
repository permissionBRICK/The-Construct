using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Microsoft.Data.Sqlite;

namespace Constructd.Sqlite;

/// <summary>One ledger gate and one IMMEDIATE transaction for all accepted intent.</summary>
public sealed class SqliteAdmissionStore(SqliteCapacityLedger ledger, IClock clock) : IAdmissionStore
{
    private static AdmissionResult Result(AdmissionOutcome outcome, OperationKeyRecord? key = null,
        CapacityDecision? capacity = null, CascadeAcceptance? cascade = null) => new(outcome, key, capacity, cascade, capacity?.ReservationIds ?? []);

    private static AdmissionResult? Key(SqliteCapacityLedger.Transaction tx, OperationKeyRecord? key, bool completing)
    {
        if (key is null) return null;
        var (outcome, existing) = SqliteOperationKeyStore.InsertInTransaction(tx.Connection, tx.Sql, key);
        if (outcome == OperationKeyOutcome.Conflict) return Result(AdmissionOutcome.KeyConflict, existing);
        if (outcome == OperationKeyOutcome.Replay && (!completing || existing!.State == OperationKeyState.Completed))
            return Result(AdmissionOutcome.Replay, existing);
        return null;
    }

    public async Task<AdmissionResult> AdmitAsync(AdmissionPlan plan, CancellationToken ct)
    {
        await using var tx = plan.Reservation is null ? await ledger.BeginMutationAsync(ct) : await ledger.BeginAsync(ct);
        if (Key(tx, plan.OperationKey, false) is { } prior) return prior;
        if (plan.VmToInsert is { } vm)
        {
            var added = await SqliteVmRepository.AddInTransaction(tx.Connection, tx.Sql, vm,
                plan.Allowance ?? throw new ArgumentException("VM admission requires allowance."), ct);
            if (added != VmAddDecision.Added) return Result(added switch
            {
                VmAddDecision.NameTaken => AdmissionOutcome.NameTaken,
                VmAddDecision.ParentClosed => AdmissionOutcome.ParentClosed,
                VmAddDecision.ParentMissing => AdmissionOutcome.ParentMissing,
                _ => AdmissionOutcome.QuotaExceeded
            });
        }
        foreach (var item in plan.MediaToInsert) SqliteMediaStore.InsertInTransaction(tx.Connection, tx.Sql, item);
        foreach (var upload in plan.UploadsToInsert) SqliteMediaStore.InsertInTransaction(tx.Connection, tx.Sql, upload);
        foreach (var reference in plan.ReferencesToInsert)
            if (!SqliteMediaStore.InsertInTransaction(tx.Connection, tx.Sql, reference)) return Result(AdmissionOutcome.MediaNotReady);
        CapacityDecision? decision = null;
        if (plan.Reservation is { } request)
        {
            decision = tx.ReserveInTransaction(request);
            if (!decision.Allowed) return Result(AdmissionOutcome.CapacityRefused, capacity: decision);
        }
        CascadeAcceptance? cascade = null;
        if (plan.CascadeToAccept is { } preview)
        {
            cascade = await SqliteVmRepository.AcceptCascadeInTransaction(tx.Connection, tx.Sql, preview.Parent,
                preview.Token, plan.FenceJobId ?? throw new ArgumentException("Cascade requires a job."), clock.UtcNow, ct);
            if (!cascade.Accepted) return Result(AdmissionOutcome.CascadeMismatch, cascade: cascade);
        }
        else if (plan.VmToFence is { } name)
        {
            if (plan.FenceJobId is null) return Result(AdmissionOutcome.VersionConflict);
            if (!await SqliteVmRepository.TryFenceInTransaction(tx.Connection, tx.Sql, name, plan.FenceJobId, plan.CloseChildCreation, ct))
                return Result(AdmissionOutcome.VersionConflict);
        }
        if (plan.JobToInsert is { } job)
        {
            using var exists = Command(tx, "SELECT COUNT(*) FROM jobs WHERE id=@id"); exists.With("@id", job.Id);
            if (job.State != JobState.Queued || Convert.ToInt64(exists.ExecuteScalar()) != 0) return Result(AdmissionOutcome.VersionConflict);
            await SqliteJobStore.WriteInTransaction(tx.Connection, tx.Sql, job, true, ct);
        }
        ct.ThrowIfCancellationRequested();
        tx.Commit();
        return Result(AdmissionOutcome.Accepted, capacity: decision, cascade: cascade);
    }

    public async Task<AdmissionResult> MutateAsync(OperationKeyRecord? key, Func<IAdmissionScope, Task<bool>> mutation, CancellationToken ct)
    {
        // A mutation may re-admit swept reservations, so refresh inventory before opening SQLite.
        await using var tx = await ledger.BeginAsync(ct);
        if (Key(tx, key, true) is { } prior) return prior;
        var scope = new Scope(tx, clock, ct);
        try
        {
            if (!await mutation(scope) || scope.Conflict) return Result(AdmissionOutcome.VersionConflict);
            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return Result(AdmissionOutcome.Accepted);
        }
        finally { scope.Active = false; }
    }

    public async Task MarkStartFailedAsync(string jobId, string error, CancellationToken ct)
    {
        await using var tx = await ledger.BeginMutationAsync(ct);
        using var exists = Command(tx, "SELECT COUNT(*) FROM jobs WHERE id=@id"); exists.With("@id", jobId);
        if (Convert.ToInt64(exists.ExecuteScalar()) == 0) throw new KeyNotFoundException("Unknown queued job.");
        using var cmd = Command(tx, "UPDATE jobs SET state='Failed',error='Persisted job could not start.',finished=@at WHERE id=@id AND state='Queued'");
        cmd.With("@id", jobId).With("@at", SqliteDatabase.Text(clock.UtcNow)); cmd.ExecuteNonQuery(); tx.Commit();
    }

    private static SqliteCommand Command(SqliteCapacityLedger.Transaction tx, string sql)
    { var cmd = tx.Connection.CreateCommand(); cmd.Transaction = tx.Sql; cmd.CommandText = sql; return cmd; }

    private sealed class Scope(SqliteCapacityLedger.Transaction tx, IClock clock, CancellationToken ct) : IAdmissionScope
    {
        internal bool Active = true;
        internal bool Conflict;
        private void Check() { if (!Active) throw new InvalidOperationException("Admission scope is no longer active."); ct.ThrowIfCancellationRequested(); }
        private bool Cas(bool value) { Conflict |= !value; return value; }
        public async Task<bool> UpdateLeaseAsync(string vmName, Lease lease, long expectedVersion)
        { Check(); return Cas(await SqliteVmRepository.UpdateLeaseInTransaction(tx.Connection, tx.Sql, vmName, lease, expectedVersion, ct)); }
        public Task<bool> UpdateSharingAsync(string vmName, SharingScope scope)
        {
            Check(); using var cmd = Command(tx, "UPDATE vms SET sharing=@scope WHERE name=@name AND kind='child' AND deleting=0");
            cmd.With("@name", vmName).With("@scope", WireJson.Enum(scope)); return Task.FromResult(Cas(cmd.ExecuteNonQuery() == 1));
        }
        public Task SetOverrideAsync(VmOverride value)
        { Check(); return SqliteVmRepository.SetOverrideInTransaction(tx.Connection, tx.Sql, value, ct); }
        public async Task<bool> SetAllowanceAsync(string userName, UserAllowance allowance)
        { Check(); return Cas(await SqliteUserStore.SetAllowanceInTransaction(tx.Connection, tx.Sql, userName, allowance, ct)); }
        public Task<bool> BumpPowerGenerationAsync(string vmName, long expected)
        {
            Check(); using var cmd = Command(tx, "UPDATE vms SET power_generation=power_generation+1 WHERE name=@name AND power_generation=@expected");
            cmd.With("@name", vmName).With("@expected", expected); return Task.FromResult(Cas(cmd.ExecuteNonQuery() == 1));
        }
        public Task<Vm?> ReadVmAsync(string vmName)
        { Check(); return SqliteVmRepository.ReadInTransaction(tx.Connection, tx.Sql, vmName, ct); }
        public Task<CapacityDecision> ReserveAsync(ReservationRequest request)
        { Check(); return Task.FromResult(tx.ReserveInTransaction(request)); }
        public Task ConfirmReservationsAsync(IReadOnlyList<string> ids, VmState observed)
        { Check(); tx.ConfirmInTransaction(ids, observed); return Task.CompletedTask; }
        public Task ReleaseReservationsAsync(IReadOnlyList<string> ids, VmState observed, string reason)
        { Check(); tx.ReleaseInTransaction(ids, observed, reason); return Task.CompletedTask; }
        public Task<bool> CompleteOperationKeyAsync(string owner, string kind, string key, string responseJson)
        { Check(); return Task.FromResult(Cas(SqliteOperationKeyStore.CompleteInTransaction(tx.Connection, tx.Sql, owner, kind, key, responseJson, clock.UtcNow))); }
        public Task AppendAuditAsync(AuditEntry entry)
        { Check(); return SqliteAuditLog.AppendInTransaction(tx.Connection, tx.Sql, entry, ct); }

    }
}
