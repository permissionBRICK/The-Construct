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
        tx.Sql.Save("admission");
        if (Key(tx, plan.OperationKey, false) is { } prior) return prior;
        var newPrimary = plan.VmToInsert is { Kind: VmKind.Primary } candidate && !tx.Vms.Any(v => Constructd.Core.Logic.Ownership.SameName(v.Name, candidate.Name));
        if (plan.VmToInsert is { } vm)
        {
            var allowance = plan.Allowance ?? throw new ArgumentException("VM admission requires allowance.");
            if (vm.Kind == VmKind.Child && plan.OwnerChildrenLimit is int ownerLimit)
            {
                var parentCount = tx.Vms.Count(v => Constructd.Core.Logic.Ownership.SameName(v.Parent, vm.Parent));
                if (parentCount >= allowance.MaxRetainedChildren)
                    return Result(AdmissionOutcome.QuotaExceeded, capacity: new(false, [], "children", "user", 1,
                        allowance.MaxRetainedChildren, Math.Max(0, allowance.MaxRetainedChildren - parentCount), "parent-child-limit", tx.Snapshot.Epoch));
                var ownerCount = tx.Vms.Count(v => v.Kind == VmKind.Child && Constructd.Core.Logic.Ownership.SameName(v.Owner, vm.Owner));
                if (ownerCount >= ownerLimit)
                    return Result(AdmissionOutcome.QuotaExceeded, capacity: new(false, [], "children", "user", 1,
                        ownerLimit, Math.Max(0, ownerLimit - ownerCount), "owner-child-limit", tx.Snapshot.Epoch));
                allowance = allowance with { MaxRetainedChildren = ownerLimit };
            }
            var added = await SqliteVmRepository.AddInTransaction(tx.Connection, tx.Sql, vm,
                allowance, ct);
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
            if (newPrimary && plan.JobToInsert is { Kind: "create-vm" }) request = tx.AdoptAbandonedPrimaryStorage(request);
            decision = Reserve(tx, request);
            if (!decision.Allowed)
            {
                tx.Sql.Rollback("admission");
                tx.Audit("capacity.refuse", request.Owner, request.VmName ?? request.OperationId, decision.Reason, true);
                tx.Commit();
                return Result(AdmissionOutcome.CapacityRefused, capacity: decision);
            }
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
        if (plan.VmToAssignJob is { } target)
        {
            if (plan.JobToInsert is null) return Result(AdmissionOutcome.VersionConflict);
            using var command = Command(tx, """
                UPDATE vms SET current_job_id=@job WHERE name=@name AND deleting=0 AND
                (current_job_id IS NULL OR NOT EXISTS(SELECT 1 FROM jobs WHERE id=vms.current_job_id AND state IN ('Queued','Running')))
                """);
            command.With("@name", target).With("@job", plan.JobToInsert.Id);
            if (command.ExecuteNonQuery() != 1) return Result(AdmissionOutcome.VersionConflict);
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
        tx.Sql.Save("mutation");
        if (Key(tx, key, true) is { } prior) return prior;
        var scope = new Scope(tx, clock, ct);
        try
        {
            if (!await mutation(scope) || scope.Conflict)
            {
                if (scope.Refusal is { } refusal)
                {
                    tx.Sql.Rollback("mutation");
                    tx.Audit("capacity.refuse", refusal.Request.Owner, refusal.Request.VmName ?? refusal.Request.OperationId, refusal.Decision.Reason, true);
                    tx.Commit();
                }
                return Result(AdmissionOutcome.VersionConflict);
            }
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

    private static CapacityDecision Reserve(SqliteCapacityLedger.Transaction tx, ReservationRequest request)
    {
        try { return tx.ReserveInTransaction(request); }
        catch (ReservationConflictException) { return new(false, [], null, null, 0, 0, 0, "reservation-conflict", tx.Snapshot.Epoch); }
    }
    private static SqliteCommand Command(SqliteCapacityLedger.Transaction tx, string sql)
    { var cmd = tx.Connection.CreateCommand(); cmd.Transaction = tx.Sql; cmd.CommandText = sql; return cmd; }

    private sealed class Scope(SqliteCapacityLedger.Transaction tx, IClock clock, CancellationToken ct) : IAdmissionScope
    {
        internal bool Active = true;
        internal bool Conflict;
        internal (ReservationRequest Request, CapacityDecision Decision)? Refusal;
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
        public Task<bool> UpdateHardwareAsync(string vmName, ChildHardware hardware, long expectedGeneration)
        {
            Check(); using var cmd = Command(tx, "UPDATE vms SET hardware_json=@hardware,cpu=@cpu,ram_mb=@ram,disk_gb=@disk,power_generation=power_generation+1 WHERE name=@name AND kind='child' AND deleting=0 AND power_generation=@expected");
            cmd.With("@name", vmName).With("@hardware", WireJson.Serialize(hardware)).With("@cpu", hardware.Cpus)
                .With("@ram", hardware.RamMb).With("@disk", hardware.DiskGb).With("@expected", expectedGeneration);
            return Task.FromResult(Cas(cmd.ExecuteNonQuery() == 1));
        }
        public Task<bool> UpdatePrimaryRamAsync(string vmName, int ramGb, long expectedGeneration)
        {
            Check(); using var cmd = Command(tx, "UPDATE vms SET ram_gb=@ram WHERE name=@name AND kind='primary' AND deleting=0 AND power_generation=@expected");
            cmd.With("@name", vmName).With("@ram", ramGb).With("@expected", expectedGeneration);
            return Task.FromResult(Cas(cmd.ExecuteNonQuery() == 1));
        }
        public Task<bool> UpdatePrimaryIdleAsync(string vmName, IdlePolicy policy, long expectedGeneration)
        {
            Check(); using var cmd = Command(tx, "UPDATE vms SET idle_timeout_minutes=@timeout,idle_action=@action WHERE name=@name AND deleting=0 AND power_generation=@expected");
            cmd.With("@name", vmName).With("@timeout", policy.TimeoutMinutes).With("@action", policy.Action.ToString()).With("@expected", expectedGeneration);
            return Task.FromResult(Cas(cmd.ExecuteNonQuery() == 1));
        }
        public Task<bool> UpdatePrimaryCpuAsync(string vmName, int cpus, long expectedGeneration)
        {
            Check(); using var cmd = Command(tx, "UPDATE vms SET cpu=@cpu WHERE name=@name AND kind='primary' AND deleting=0 AND power_generation=@expected");
            cmd.With("@name", vmName).With("@cpu", cpus).With("@expected", expectedGeneration);
            return Task.FromResult(Cas(cmd.ExecuteNonQuery() == 1));
        }
        public Task<bool> UpdatePowerStateAsync(string vmName, VmState state, long expectedGeneration)
        {
            Check(); using var cmd = Command(tx, "UPDATE vms SET state=@state,power_generation=power_generation+1 WHERE name=@name AND power_generation=@expected");
            cmd.With("@name", vmName).With("@state", state.ToString()).With("@expected", expectedGeneration);
            return Task.FromResult(Cas(cmd.ExecuteNonQuery() == 1));
        }
        public Task<bool> BumpPowerGenerationAsync(string vmName, long expected)
        {
            Check(); using var cmd = Command(tx, "UPDATE vms SET power_generation=power_generation+1 WHERE name=@name AND power_generation=@expected");
            cmd.With("@name", vmName).With("@expected", expected); return Task.FromResult(Cas(cmd.ExecuteNonQuery() == 1));
        }
        public Task<Vm?> ReadVmAsync(string vmName)
        { Check(); return SqliteVmRepository.ReadInTransaction(tx.Connection, tx.Sql, vmName, ct); }
        public Task<CapacityDecision> ReserveAsync(ReservationRequest request)
        { Check(); var decision = Reserve(tx, request); if (!decision.Allowed) Refusal = (request, decision); return Task.FromResult(decision); }
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
