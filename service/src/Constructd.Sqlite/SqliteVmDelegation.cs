using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite;

public sealed partial class SqliteVmRepository
{
    public async Task<IReadOnlyList<Vm>> ListChildrenAsync(string parent, CancellationToken ct) =>
        (await ListAsync(null, ct)).Where(v => Ownership.SameName(v.Parent, parent)).ToArray();
    public async Task<IReadOnlyList<Vm>> ListSharedAsync(SharingScope scope, CancellationToken ct) =>
        (await ListAsync(null, ct)).Where(v => v.Kind == VmKind.Child && v.Sharing == scope).ToArray();
    public async Task<int> CountByOwnerAsync(string owner, VmKind kind, CancellationToken ct) =>
        (await ListAsync(owner, ct)).Count(v => v.Kind == kind);

    internal static async Task<Vm?> ReadInTransaction(SqliteConnection c, SqliteTransaction tx, string name, CancellationToken ct)
    {
        await using var command = c.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT * FROM vms WHERE name=@name"; command.With("@name", name);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<VmAddDecision> AddAsync(Vm vm, EffectiveAllowance allowance, CancellationToken ct)
    {
        await using var c = await database.OpenAsync(ct);
        await using var tx = c.BeginTransaction(deferred: false);
        var result = await AddInTransaction(c, tx, vm, allowance, ct);
        if (result == VmAddDecision.Added) await tx.CommitAsync(ct);
        return result;
    }
    internal static async Task<VmAddDecision> AddInTransaction(SqliteConnection c, SqliteTransaction tx, Vm vm, EffectiveAllowance allowance, CancellationToken ct)
    {
        if (await ReadInTransaction(c, tx, vm.Name, ct) is not null) return VmAddDecision.NameTaken;
        if (vm.Kind == VmKind.Child)
        {
            var parent = vm.Parent is null ? null : await ReadInTransaction(c, tx, vm.Parent, ct);
            if (parent is null || parent.Kind != VmKind.Primary || !Ownership.SameName(parent.Owner, vm.Owner)) return VmAddDecision.ParentMissing;
            if (parent.Deleting || parent.ChildCreationClosed) return VmAddDecision.ParentClosed;
            if (vm.Lease is null || vm.Hardware is null || vm.RamMb is null || vm.VmTokenHash is not null)
                throw new ArgumentException("A child requires hardware, RAM and lease, and cannot hold a credential.");
        }
        else if (vm.Parent is not null) throw new ArgumentException("A primary cannot have a parent.");
        await using var count = c.CreateCommand(); count.Transaction = tx;
        count.CommandText = "SELECT COUNT(*) FROM vms WHERE owner=@owner AND kind=@kind";
        count.With("@owner", vm.Owner).With("@kind", WireJson.Enum(vm.Kind));
        if (Convert.ToInt64(await count.ExecuteScalarAsync(ct)) >= (vm.Kind == VmKind.Primary ? allowance.MaxPrimaries : allowance.MaxRetainedChildren))
            return vm.Kind == VmKind.Primary ? VmAddDecision.PrimaryQuotaExceeded : VmAddDecision.ChildrenQuotaExceeded;
        await using var insert = c.CreateCommand(); insert.Transaction = tx;
        insert.CommandText = """
                INSERT INTO vms (name, owner, cpu, ram_gb, disk_gb, created, state, ssh_forward_port,
                                 vm_token_hash, idle_timeout_minutes, idle_action, deleting, power_generation, kind, parent, sharing, vm_token_kind, ram_mb, incarnation, lease_requested_text, lease_requested_seconds, lease_activated_at, lease_expires_at, lease_state, lease_version, lease_last_attempt_at, lease_last_outcome, hardware_json, guest_construct_commit, guest_provisioned_at, guest_reinstalled_at, guest_reported_at, guest_provenance, guest_last_attempt_at, guest_last_attempt_outcome, observed_created_at, observed_last_boot_at, observed_addresses_json, observed_storage_problem, child_creation_closed, current_job_id)
                VALUES (@name, @owner, @cpu, @ramGb, @diskGb, @created, @state, @sshForwardPort,
                        @vmTokenHash, @idleTimeout, @idleAction, @deleting, @power_generation, @kind, @parent, @sharing, @vm_token_kind, @ram_mb, @incarnation, @lease_requested_text, @lease_requested_seconds, @lease_activated_at, @lease_expires_at, @lease_state, @lease_version, @lease_last_attempt_at, @lease_last_outcome, @hardware_json, @guest_construct_commit, @guest_provisioned_at, @guest_reinstalled_at, @guest_reported_at, @guest_provenance, @guest_last_attempt_at, @guest_last_attempt_outcome, @observed_created_at, @observed_last_boot_at, @observed_addresses_json, @observed_storage_problem, @child_creation_closed, @current_job_id);
                """;
        Bind(insert, vm); await insert.ExecuteNonQueryAsync(ct);
        return VmAddDecision.Added;
    }

    public async Task<bool> TryFenceAsync(string name, string jobId, bool closeChildCreation, CancellationToken ct)
    {
        await using var c = await database.OpenAsync(ct);
        await using var tx = c.BeginTransaction(deferred: false);
        var result = await TryFenceInTransaction(c, tx, name, jobId, closeChildCreation, ct);
        await tx.CommitAsync(ct); return result;
    }
    internal static async Task<bool> TryFenceInTransaction(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string name, string jobId, bool closeChildCreation, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE vms SET deleting=1, vm_token_hash=NULL, current_job_id=@job,
              child_creation_closed=CASE WHEN @closed=1 THEN 1 ELSE child_creation_closed END
            WHERE name=@name AND (current_job_id IS NULL OR NOT EXISTS
              (SELECT 1 FROM jobs WHERE id=vms.current_job_id AND state IN ('Queued','Running')))
            """;
        cmd.With("@name", name).With("@job", jobId).With("@closed", closeChildCreation);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
    public async Task<bool> UpdateLeaseAsync(string name, Lease lease, long expectedVersion, CancellationToken ct)
    {
        await using var c = await database.OpenAsync(ct); await using var tx = c.BeginTransaction(deferred: false);
        var result = await UpdateLeaseInTransaction(c, tx, name, lease, expectedVersion, ct);
        await tx.CommitAsync(ct); return result;
    }
    internal static async Task<bool> UpdateLeaseInTransaction(SqliteConnection c, SqliteTransaction tx, string name, Lease lease, long expectedVersion, CancellationToken ct)
    {
        if (lease.Version != expectedVersion + 1) return false;
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE vms SET lease_requested_text=@text, lease_requested_seconds=@seconds,
              lease_activated_at=@activated, lease_expires_at=@expires, lease_state=@state,
              lease_version=@version, lease_last_attempt_at=@attempt, lease_last_outcome=@outcome
            WHERE name=@name AND kind='child' AND lease_version=@expected
            """;
        cmd.With("@name", name).With("@expected", expectedVersion).With("@text", lease.RequestedText)
            .With("@seconds", lease.RequestedSeconds).With("@activated", SqliteDatabase.TextOrNull(lease.ActivatedAt))
            .With("@expires", SqliteDatabase.TextOrNull(lease.ExpiresAt)).With("@state", WireJson.Enum(lease.State))
            .With("@version", lease.Version).With("@attempt", SqliteDatabase.TextOrNull(lease.LastExpiryAttemptAt)).With("@outcome", lease.LastExpiryOutcome);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
    public async Task<IReadOnlyList<Vm>> ListLeasesDueAsync(DateTimeOffset now, TimeSpan retryAfter, CancellationToken ct) =>
        (await ListAsync(null, ct)).Where(v => v.Kind == VmKind.Child && !v.Deleting &&
            LeaseRules.RetryDue(v.Lease, now, retryAfter)).ToArray();

    public async Task<bool> UpdateGuestReportAsync(string name, GuestReport report, CancellationToken ct)
    {
        await using var c = await database.OpenAsync(ct); await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE vms SET guest_construct_commit=COALESCE(@commit,guest_construct_commit),
              guest_provisioned_at=COALESCE(@provisioned,guest_provisioned_at), guest_reinstalled_at=COALESCE(@reinstalled,guest_reinstalled_at),
              guest_reported_at=COALESCE(@reported,guest_reported_at),
              guest_provenance=CASE WHEN @provenance='unknown' THEN guest_provenance ELSE @provenance END,
              guest_last_attempt_at=COALESCE(@attempt,guest_last_attempt_at), guest_last_attempt_outcome=COALESCE(@outcome,guest_last_attempt_outcome)
            WHERE name=@name AND kind='primary' AND deleting=0
            """;
        cmd.With("@name", name).With("@commit", report.ConstructCommit).With("@provisioned", SqliteDatabase.TextOrNull(report.ProvisionedAt))
            .With("@reinstalled", SqliteDatabase.TextOrNull(report.ReinstalledAt)).With("@reported", SqliteDatabase.TextOrNull(report.ReportedAt))
            .With("@provenance", WireJson.Enum(report.Provenance)).With("@attempt", SqliteDatabase.TextOrNull(report.LastAttemptAt)).With("@outcome", report.LastAttemptOutcome);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
    public async Task<bool> UpdateObservationAsync(string name, HostObservation observation, CancellationToken ct)
    {
        await using var c = await database.OpenAsync(ct); await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE vms SET observed_created_at=@created,observed_last_boot_at=@boot,observed_addresses_json=@addresses,observed_storage_problem=@problem WHERE name=@name";
        cmd.With("@name", name).With("@created", SqliteDatabase.TextOrNull(observation.CreatedAt)).With("@boot", SqliteDatabase.TextOrNull(observation.LastBootAt))
            .With("@addresses", WireJson.Serialize(observation.Addresses)).With("@problem", observation.StorageProblem);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
    public async Task<VmOverride?> GetOverrideAsync(string vmName, CancellationToken ct)
    {
        await using var c = await database.OpenAsync(ct); await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM vm_overrides WHERE vm_name=@name"; cmd.With("@name", vmName);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new(r.GetString("vm_name"), r.GetBoolOrNull("allow_child_creation"), r.GetIntOrNull("max_retained_children"),
            r.GetLongOrNull("max_child_lifetime_seconds"), r.GetBoolOrNull("allow_never_lifetime"), r.GetBoolOrNull("allow_sharing"), r.GetTime("updated_at")!.Value) : null;
    }
    public async Task SetOverrideAsync(VmOverride value, CancellationToken ct)
    {
        await using var c = await database.OpenAsync(ct); await using var tx = c.BeginTransaction(deferred: false);
        await SetOverrideInTransaction(c, tx, value, ct);
        await tx.CommitAsync(ct);
    }
    internal static async Task SetOverrideInTransaction(SqliteConnection c, SqliteTransaction tx, VmOverride value, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO vm_overrides VALUES (@name,@creation,@children,@lifetime,@never,@sharing,@at)
            ON CONFLICT(vm_name) DO UPDATE SET allow_child_creation=@creation,max_retained_children=@children,
              max_child_lifetime_seconds=@lifetime,allow_never_lifetime=@never,allow_sharing=@sharing,updated_at=@at
            """;
        cmd.With("@name", value.VmName).With("@creation", value.AllowChildCreation).With("@children", value.MaxRetainedChildren)
            .With("@lifetime", value.MaxChildLifetimeSeconds).With("@never", value.AllowNeverLifetime).With("@sharing", value.AllowSharing).With("@at", SqliteDatabase.Text(value.UpdatedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<bool> RemoveOverrideAsync(string vmName, CancellationToken ct)
    {
        await using var c = await database.OpenAsync(ct); await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM vm_overrides WHERE vm_name=@name"; cmd.With("@name", vmName);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
    public async Task<CascadePreview> SaveCascadePreviewAsync(CascadePreview preview, CancellationToken ct)
    {
        await using var c = await database.OpenAsync(ct); await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cascades VALUES (@parent,@incarnation,@token,@issued,@expires,@children,@state,@job,@outcomes)
            ON CONFLICT(parent) DO UPDATE SET parent_incarnation=@incarnation,token=@token,issued_at=@issued,
              expires_at=@expires,children_json=@children,state=@state,job_id=@job,outcomes_json=@outcomes
            """;
        cmd.With("@parent", preview.Parent).With("@incarnation", preview.ParentIncarnation).With("@token", preview.Token)
            .With("@issued", SqliteDatabase.Text(preview.IssuedAt)).With("@expires", SqliteDatabase.Text(preview.ExpiresAt))
            .With("@children", WireJson.Serialize(preview.Children)).With("@state", WireJson.Enum(preview.State)).With("@job", preview.JobId).With("@outcomes", WireJson.Serialize(preview.Outcomes));
        await cmd.ExecuteNonQueryAsync(ct); return preview;
    }
    private static CascadePreview ReadCascade(SqliteDataReader r) => new(r.GetString("parent"), r.GetStringOrNull("parent_incarnation"), r.GetString("token"),
        r.GetTime("issued_at")!.Value, r.GetTime("expires_at")!.Value, WireJson.Read<IReadOnlyList<CascadeChild>>(r.GetString("children_json"))!,
        r.GetEnum<CascadeState>("state"), r.GetStringOrNull("job_id"), WireJson.Read<IReadOnlyDictionary<string, string>>(r.GetString("outcomes_json"))!);
    public async Task<CascadePreview?> GetCascadePreviewAsync(string parent, CancellationToken ct)
    {
        await using var c = await database.OpenAsync(ct); await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM cascades WHERE parent=@parent"; cmd.With("@parent", parent);
        await using var r = await cmd.ExecuteReaderAsync(ct); return await r.ReadAsync(ct) ? ReadCascade(r) : null;
    }
    public async Task<CascadeAcceptance> TryAcceptCascadeAsync(string parent, string token, string jobId, CancellationToken ct)
    {
        await using var c = await database.OpenAsync(ct); await using var tx = c.BeginTransaction(deferred: false);
        var result = await AcceptCascadeInTransaction(c, tx, parent, token, jobId, clock?.UtcNow ?? DateTimeOffset.UtcNow, ct);
        if (result.Accepted) await tx.CommitAsync(ct);
        return result;
    }
    internal static async Task<CascadeAcceptance> AcceptCascadeInTransaction(SqliteConnection c, SqliteTransaction tx, string parent, string token, string jobId, DateTimeOffset now, CancellationToken ct)
    {
        var vm = await ReadInTransaction(c, tx, parent, ct);
        var children = new List<Vm>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx; cmd.CommandText = "SELECT * FROM vms WHERE parent=@parent"; cmd.With("@parent", parent);
            await using var r = await cmd.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) children.Add(Read(r));
        }
        CascadePreview? preview;
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx; cmd.CommandText = "SELECT * FROM cascades WHERE parent=@parent"; cmd.With("@parent", parent);
            await using var r = await cmd.ExecuteReaderAsync(ct); preview = await r.ReadAsync(ct) ? ReadCascade(r) : null;
        }
        var current = CascadeRules.Children(children);
        await using (var live = c.CreateCommand())
        {
            live.Transaction = tx;
            live.CommandText = """
                SELECT COUNT(*) FROM vms v JOIN jobs j ON j.id=v.current_job_id
                WHERE (v.name=@parent OR v.parent=@parent) AND j.state IN ('Queued','Running')
                """;
            live.With("@parent", parent);
            if (Convert.ToInt64(await live.ExecuteScalarAsync(ct)) > 0) return new(false, "operation-in-progress", current, null);
        }

        if (vm is null || preview is null || !CascadeRules.Matches(preview, vm, current, token, now))
            return new(false, "cascade-mismatch", current, null);
        await using var fence = c.CreateCommand(); fence.Transaction = tx;
        fence.CommandText = """
            UPDATE vms SET deleting=1,vm_token_hash=NULL,current_job_id=@job,
              child_creation_closed=CASE WHEN name=@parent THEN 1 ELSE child_creation_closed END
            WHERE name=@parent OR parent=@parent;
            UPDATE cascades SET state='accepted',job_id=@job WHERE parent=@parent;
            """;
        fence.With("@parent", parent).With("@job", jobId); await fence.ExecuteNonQueryAsync(ct);
        return new(true, null, current, null);
    }
    public async Task<bool> UpdateIncarnationAsync(string name, string incarnation, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incarnation);
        await using var c = await database.OpenAsync(ct); await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE vms SET incarnation=@id WHERE name=@name AND (incarnation IS NULL OR incarnation=@id)";
        cmd.With("@name", name).With("@id", incarnation); return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
    public async Task<bool> SetTokenAsync(string name, string? hash, VmTokenKind kind, CancellationToken ct)
    {
        await using var c = await database.OpenAsync(ct); await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE vms SET vm_token_hash=@hash,vm_token_kind=CASE WHEN @hash IS NULL THEN vm_token_kind ELSE @kind END WHERE name=@name AND kind='primary' AND deleting=0";
        cmd.With("@name", name).With("@hash", hash).With("@kind", WireJson.Enum(kind)); return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> RestoreUnqueuedDeletionAsync(Vm original, CancellationToken ct)
    {
        if (original.Deleting) return false;
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE vms SET deleting=0, vm_token_hash=@hash, vm_token_kind=@kind
            WHERE name=@name AND kind='primary' AND deleting=1 AND child_creation_closed=0
              AND current_job_id IS NULL AND incarnation IS @incarnation;
            """;
        command.With("@name", original.Name).With("@hash", original.VmTokenHash)
            .With("@kind", original.TokenKind.ToString().ToLowerInvariant()).With("@incarnation", original.Incarnation);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
}
