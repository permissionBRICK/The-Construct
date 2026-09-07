using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Microsoft.Data.Sqlite;

namespace Constructd.Sqlite;

/// <summary>All writers, including the admission coordinator, use BeginAsync and its one IMMEDIATE transaction.</summary>
public sealed partial class SqliteCapacityLedger(SqliteDatabase database, IClock clock, IHypervisorInventory inventory,
    ConstructdOptions options) : ICapacityLedger
{
    private readonly IClock _clock = clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InventorySnapshot? _snapshot;
    private DateTimeOffset _readAt;
    private bool _invalidated = true;
    public Func<CancellationToken, Task<IReadOnlyList<OrphanOutcome>>>? Reconcile { get; set; }

    /// <summary>Acquire after VM/media gates. Inventory I/O finishes BEFORE the SQLite transaction opens.
    /// The returned scope must be disposed; Commit is explicit. No other ledger method may be called inside it.</summary>
    public Task<Transaction> BeginAsync(CancellationToken ct, bool refresh = false) => BeginCoreAsync(ct, true, refresh);

    /// <summary>Database-only mutation: no inventory I/O. Use BeginAsync when the scope can reserve.</summary>
    public Task<Transaction> BeginMutationAsync(CancellationToken ct) => BeginCoreAsync(ct, false, false);

    private async Task<Transaction> BeginCoreAsync(CancellationToken ct, bool admission, bool refresh)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (admission || refresh) using (var read = database.Open())
            {
                var config = Config(read, null);
                if (refresh || _invalidated || _snapshot is null || _clock.UtcNow - _readAt >= TimeSpan.FromSeconds(config.ReconcileSeconds))
                {
                    try { _snapshot = await inventory.ReadAsync(ReadRows(read, null), ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { _snapshot = new(0, _clock.UtcNow, new(0, 0, 0, [], _clock.UtcNow), [], false, ["inventory-unavailable"]); }
                    _readAt = _clock.UtcNow;
                    _invalidated = false;
                }
            }
            var connection = database.Open();
            try { return new(this, connection, connection.BeginTransaction(deferred: false), _snapshot ?? new(0, DateTimeOffset.MinValue, new(0, 0, 0, [], DateTimeOffset.MinValue), [], false, ["inventory-not-yet-read"]), ct, admission); }
            catch { connection.Dispose(); throw; }
        }
        catch { _gate.Release(); throw; }
    }

    public async Task<CapacityDecision> TryReserveAsync(ReservationRequest request, CancellationToken ct)
    {
        ReservationRules.Validate(request);
        await using var transaction = await BeginAsync(ct);
        var decision = transaction.ReserveInTransaction(request);
        transaction.Commit();
        return decision;
    }
    public async Task ConfirmAsync(IReadOnlyList<string> ids, VmState observed, CancellationToken ct)
    { await using var tx = await BeginMutationAsync(ct); tx.ConfirmInTransaction(ids, observed); tx.Commit(); }
    public async Task ReleaseAsync(IReadOnlyList<string> ids, VmState observed, string reason, CancellationToken ct)
    { await using var tx = await BeginMutationAsync(ct); tx.ReleaseInTransaction(ids, observed, reason); tx.Commit(); }
    public async Task TrimAsync(string id, long amount, CancellationToken ct)
    {
        await using var tx = await BeginMutationAsync(ct);
        var row = tx.Rows.SingleOrDefault(r => r.Id == id) ?? throw new KeyNotFoundException("Unknown reservation.");
        if (row.Resource != ReservationResource.Storage || amount < 0 || amount > row.Amount) throw new ArgumentException("Only storage may shrink.");
        tx.Put(row with { Amount = amount }); tx.Audit("capacity.trim", row.ScopeOwner, row.VmName ?? id); tx.Commit();
    }
    public async Task ExtendAsync(IReadOnlyList<string> ids, TimeSpan by, CancellationToken ct)
    {
        if (by <= TimeSpan.Zero) throw new ArgumentException("Extension must be positive.");
        await using var tx = await BeginMutationAsync(ct);
        foreach (var row in tx.Rows.Where(r => ids.Contains(r.Id) && r.Phase == ReservationPhase.Pending))
        {
            tx.Put(row with { PendingUntil = (row.PendingUntil > _clock.UtcNow ? row.PendingUntil.Value : _clock.UtcNow) + by });
            tx.Audit("capacity.extend", row.ScopeOwner, row.VmName ?? row.Id);
        }
        tx.Commit();
    }
    public async Task<HostCapacitySnapshot> SnapshotAsync(bool refresh, CancellationToken ct)
    { await using var tx = await BeginCoreAsync(ct, false, refresh); return tx.Snapshot; }
    public Task<IReadOnlyList<OrphanOutcome>> ReconcileAsync(CancellationToken ct) => Reconcile?.Invoke(ct)
        ?? Task.FromResult<IReadOnlyList<OrphanOutcome>>([]);

    private CapacityConfig Config(SqliteConnection c, SqliteTransaction? tx) => Section<CapacityConfig>(c, tx, "capacity")
        ?? HostAdminDefaults.Capacity with { Mode = options.HostAdmin.Capacity.Mode };
    private static T? Section<T>(SqliteConnection c, SqliteTransaction? tx, string key) where T : class
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT value_json FROM host_config WHERE key=@key"; cmd.With("@key", key);
        return WireJson.Read<T>((string?)cmd.ExecuteScalar());
    }
    private static IReadOnlyList<Reservation> ReadRows(SqliteConnection c, SqliteTransaction? tx)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT * FROM reservations ORDER BY id";
        using var r = cmd.ExecuteReader(); var rows = new List<Reservation>();
        while (r.Read()) rows.Add(new(r.GetString("id"), SqliteDatabase.ReadEnum<ReservationResource>(r.GetString("resource")),
            r.GetStringOrNull("scope_owner"), r.GetStringOrNull("vm_name"), r.GetStringOrNull("artifact"), r.GetStringOrNull("volume"), r.GetInt64(r.GetOrdinal("amount")),
            SqliteDatabase.ReadEnum<ReservationPhase>(r.GetString("phase")), SqliteDatabase.ReadEnum<ReservationOrigin>(r.GetString("origin")),
            r.GetStringOrNull("operation_id"), SqliteDatabase.ReadTime(r.GetString("created")), r.GetTime("pending_until"), r.GetTime("confirmed_at")));
        return rows;
    }

    public sealed class Transaction : IAsyncDisposable
    {
        private readonly SqliteCapacityLedger _ledger;
        private readonly CancellationToken _ct;
        private bool _disposed;
        private bool _committed;
        private bool _dirty;
        private readonly InventorySnapshot _inventory;
        private readonly bool _canReserve;
        public SqliteConnection Connection { get; }
        public SqliteTransaction Sql { get; }
        internal Transaction(SqliteCapacityLedger ledger, SqliteConnection connection, SqliteTransaction sql, InventorySnapshot inventory, CancellationToken ct, bool canReserve)
        { _canReserve = canReserve; _ledger = ledger; Connection = connection; Sql = sql; _inventory = inventory; _ct = ct; }
        private void Check() { ObjectDisposedException.ThrowIf(_disposed || _committed, this); _ct.ThrowIfCancellationRequested(); }
        public IReadOnlyList<Reservation> Rows { get { Check(); return ReadRows(Connection, Sql); } }
        public IReadOnlyList<Vm> Vms
        {
            get
            {
                Check(); using var cmd = Connection.CreateCommand(); cmd.Transaction = Sql; cmd.CommandText = "SELECT * FROM vms";
                using var r = cmd.ExecuteReader(); var result = new List<Vm>();
                while (r.Read()) result.Add(new(r.GetString("name"), r.GetString("owner"), r.GetInt("cpu"), r.GetInt("ram_gb"), r.GetInt("disk_gb"),
                    SqliteDatabase.ReadTime(r.GetString("created")), SqliteDatabase.ReadEnum<VmState>(r.GetString("state")), null, null,
                    new(0, IdleAction.Off), [], r.GetBool("deleting"), r.GetInt64(r.GetOrdinal("power_generation")),
                    SqliteDatabase.ReadEnum<VmKind>(r.GetString("kind")), r.GetStringOrNull("parent"), RamMb: r.GetIntOrNull("ram_mb"),
                    Incarnation: r.GetStringOrNull("incarnation"), CurrentJobId: r.GetStringOrNull("current_job_id")));
                return result;
            }
        }
        public HostCapacitySnapshot Snapshot => CapacityMath.Calculate(_inventory, _ledger.Config(Connection, Sql), Rows, Vms);
        private EffectiveAllowance? Allowance(string owner)
        {
            var defaults = Section<UserDefaultsConfig>(Connection, Sql, "userDefaults") ?? HostAdminDefaults.UserDefaults;
            var caps = Section<UserCapsConfig>(Connection, Sql, "userCaps") ?? HostAdminDefaults.UserCaps;
            using var cmd = Connection.CreateCommand(); cmd.Transaction = Sql; cmd.CommandText = "SELECT * FROM users WHERE name=@owner"; cmd.With("@owner", owner);
            using var r = cmd.ExecuteReader(); if (!r.Read()) return null;
            static long? Min(long? a, long? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
            return new(r.GetInt("max_vms"), true, defaults.MaxRetainedChildren,
                (int?)Min(r.GetLongOrNull("cpu_budget") ?? defaults.CpuBudget, caps.CpuBudget),
                Min(r.GetLongOrNull("ram_budget_bytes") ?? defaults.RamBudgetBytes, caps.RamBudgetBytes),
                Min(r.GetLongOrNull("storage_budget_bytes") ?? defaults.StorageBudgetBytes, caps.StorageBudgetBytes), null, true, true, true);
        }
        // Only primary-create admission calls this, after proving the registry name was absent.
        // A failed create may leave a disk hold after its unchanged registry rollback. Reuse that
        // liability for the same owner/path; never take it from a live job or shrink unknown storage.
        internal void AdoptAbandonedPrimaryStorage(ReservationRequest request)
        {
            foreach (var row in Rows.Where(r => r.Resource == ReservationResource.Storage && r.OperationId is not null &&
                Ownership.SameName(r.ScopeOwner, request.Owner) && Ownership.SameName(r.VmName, request.VmName)))
            {
                if (!request.Lines.Any(l => l.Resource == row.Resource && l.Amount >= row.Amount &&
                    StringComparer.OrdinalIgnoreCase.Equals(l.Artifact, row.Artifact) && StringComparer.OrdinalIgnoreCase.Equals(l.Volume, row.Volume))) continue;
                if (_ledger.Operations?.IsAlive(row.OperationId!) == true) continue;
                using var job = Connection.CreateCommand(); job.Transaction = Sql;
                job.CommandText = "SELECT COUNT(*) FROM jobs WHERE id=@id AND kind='create-vm' AND state IN ('Failed','Cancelled')";
                job.With("@id", row.OperationId);
                if (Convert.ToInt64(job.ExecuteScalar()) != 1) continue;
                Delete(row.Id);
                Audit("capacity.adopt", request.Owner, request.VmName!, "failed-primary-create");
            }
        }
        public CapacityDecision ReserveInTransaction(ReservationRequest request)
        {
            Check();
            if (!_canReserve) throw new InvalidOperationException("Reservation requires an admission transaction.");
            ReservationRules.Validate(request);
            var rows = Rows;
            var target = Vms.FirstOrDefault(v => Ownership.SameName(v.Name, request.VmName));
            if (target is not null && !Ownership.SameName(target.Owner, request.Owner))
                throw new InvalidOperationException("Reservations must be charged to the VM owner.");
            // An operation owns one immutable resource request. Re-entry returns its existing holds.
            var prior = rows.Where(r => r.OperationId == request.OperationId).ToArray();
            if (prior.Length > 0)
            {
                var lines = request.Lines.ToList();
                foreach (var row in prior)
                {
                    var index = lines.FindIndex(l => l.Resource == row.Resource && l.Amount == row.Amount &&
                        StringComparer.OrdinalIgnoreCase.Equals(l.Artifact, row.Artifact) && StringComparer.OrdinalIgnoreCase.Equals(l.Volume, row.Volume));
                    if (index < 0 || !Ownership.SameName(row.ScopeOwner, request.Owner) || !StringComparer.OrdinalIgnoreCase.Equals(row.VmName, request.VmName))
                        throw new ReservationConflictException();
                    lines.RemoveAt(index);
                }
                if (lines.Count != 0) throw new ReservationConflictException();
                return new(true, prior.Select(r => r.Id).ToArray(), null, null, 0, 0, 0, null, _inventory.Epoch);
            }
            // The VM gate remains the structural concurrency fence. In observe mode this ledger
            // records the would-refuse decision without changing the migrated primary behavior.
            var runtimeConflict = request.VmName is not null && request.Lines.Any(l => l.Resource != ReservationResource.Storage) &&
                rows.Any(r => r.Resource != ReservationResource.Storage && Ownership.SameName(r.VmName, request.VmName));
            if (request.Lines.Where(l => l.Resource == ReservationResource.Storage).Any(l => rows.Any(r => r.Resource == ReservationResource.Storage &&
                r.Artifact is not null && CapacityMath.Key(r.Artifact) == CapacityMath.Key(l.Artifact!))))
                throw new ReservationConflictException();
            var config = _ledger.Config(Connection, Sql);
            var decision = runtimeConflict
                ? new CapacityDecision(config.Mode == CapacityMode.Observe, [], null, null, 0, 0, 0,
                    config.Mode == CapacityMode.Observe ? "observe:operation-in-progress" : "operation-in-progress", _inventory.Epoch)
                : CapacityMath.Decide(request, Snapshot, config, Allowance(request.Owner), CapacityMath.AccountedReservations(_inventory, rows, Vms));
            if (!decision.Allowed) { Audit("capacity.refuse", request.Owner, request.VmName ?? request.OperationId, decision.Reason, true); return decision; }
            var ids = new List<string>();
            foreach (var line in request.Lines)
            {
                var id = Guid.NewGuid().ToString("n"); ids.Add(id);
                Put(new(id, line.Resource, request.Owner, request.VmName, line.Artifact, line.Volume, line.Amount, ReservationPhase.Pending,
                    ReservationOrigin.Api, request.OperationId, _ledger._clock.UtcNow, _ledger._clock.UtcNow + request.PendingTimeout, null));
            }
            Audit(decision.Reason is null ? "capacity.reserve" : "capacity.observe", request.Owner, request.VmName ?? request.OperationId, decision.Reason);
            return decision with { ReservationIds = ids };
        }
        public void ConfirmInTransaction(IReadOnlyList<string> ids, VmState observed)
        {
            foreach (var row in Rows.Where(r => ids.Contains(r.Id) && ReservationRules.CanConfirm(r, observed)))
            { Put(row with { Phase = ReservationPhase.Held, ConfirmedAt = _ledger._clock.UtcNow }); Audit("capacity.confirm", row.ScopeOwner, row.VmName ?? row.Id); }
        }
        public void ReleaseInTransaction(IReadOnlyList<string> ids, VmState observed, string reason)
        {
            foreach (var row in Rows.Where(r => ids.Contains(r.Id) && ReservationRules.CanRelease(r, observed)))
            { Delete(row.Id); Audit("capacity.release", row.ScopeOwner, row.VmName ?? row.Id, "observed-" + observed.ToString().ToLowerInvariant()); }
        }
        internal void Put(Reservation r)
        {
            Check(); using var cmd = Connection.CreateCommand(); cmd.Transaction = Sql;
            cmd.CommandText = """
                INSERT INTO reservations (id,resource,scope_owner,vm_name,artifact,volume,amount,phase,origin,operation_id,pending_until,created,confirmed_at)
                VALUES (@id,@resource,@owner,@vm,@artifact,@volume,@amount,@phase,@origin,@operation,@until,@created,@confirmed)
                ON CONFLICT(id) DO UPDATE SET amount=@amount,phase=@phase,origin=@origin,pending_until=@until,confirmed_at=@confirmed
                """;
            cmd.With("@id", r.Id).With("@resource", WireJson.Enum(r.Resource)).With("@owner", r.ScopeOwner).With("@vm", r.VmName)
                .With("@artifact", r.Artifact).With("@volume", r.Volume).With("@amount", r.Amount).With("@phase", WireJson.Enum(r.Phase))
                .With("@origin", WireJson.Enum(r.Origin)).With("@operation", r.OperationId).With("@until", SqliteDatabase.TextOrNull(r.PendingUntil))
                .With("@created", SqliteDatabase.Text(r.Created)).With("@confirmed", SqliteDatabase.TextOrNull(r.ConfirmedAt));
            cmd.ExecuteNonQuery(); _dirty = true;
        }
        internal void Delete(string id)
        { Check(); using var cmd = Connection.CreateCommand(); cmd.Transaction = Sql; cmd.CommandText = "DELETE FROM reservations WHERE id=@id"; cmd.With("@id", id); cmd.ExecuteNonQuery(); _dirty = true; }
        internal void Audit(string action, string? owner, string target, string? reason = null, bool denied = false)
        {
            Check(); using var cmd = Connection.CreateCommand(); cmd.Transaction = Sql;
            cmd.CommandText = "INSERT INTO audit(at,actor,action,target,outcome,detail) VALUES(@at,@actor,@action,@target,@outcome,@detail)";
            cmd.With("@at", SqliteDatabase.Text(_ledger._clock.UtcNow)).With("@actor", owner ?? "system").With("@action", action)
                .With("@target", target).With("@outcome", denied ? "Denied" : "Success").With("@detail", reason); cmd.ExecuteNonQuery();
        }
        public void Commit() { Check(); Sql.Commit(); _committed = true; if (_dirty) _ledger._invalidated = true; }
        public ValueTask DisposeAsync()
        {
            if (!_disposed) { _disposed = true; try { Sql.Dispose(); } finally { try { Connection.Dispose(); } finally { _ledger._gate.Release(); } } }
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class ReservationConflictException : InvalidOperationException { }
