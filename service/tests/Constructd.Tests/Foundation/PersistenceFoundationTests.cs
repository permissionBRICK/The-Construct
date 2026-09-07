using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Sqlite;
using Constructd.Sqlite.Migrations;
using Microsoft.Data.Sqlite;
namespace Constructd.Tests.Foundation;

public sealed class PersistenceFoundationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "construct-foundation-" + Guid.NewGuid().ToString("n"));
    private static readonly CancellationToken Ct = CancellationToken.None;
    private SqliteDatabase Database()
    { Directory.CreateDirectory(_directory); return new(Path.Combine(_directory, "test.db")); }
    private static Vm Primary(string name = "parent") => new(name, "alice", 2, 4, 40, DateTimeOffset.UtcNow, VmState.Off, 2201, "existing-hash", IdlePolicy.Disabled, []);
    private static Vm Child(string name = "child") => Primary(name) with
    {
        Kind = VmKind.Child,
        Parent = "parent",
        VmTokenHash = null,
        RamMb = 512,
        Incarnation = null,
        Hardware = new(1, 512, 10, 2, false, null, false, [], true),
        Lease = new("1h", 3600, null, null, LeaseState.Inactive, 0, null, null)
    };
    private static EffectiveAllowance Allowance => new(2, true, 3, null, null, null, null, true, true, true);


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleLifecycleSnapshotsCannotRestoreRotatedOrRevokedCredentialsOrOpenFences(bool sqlite)
    {
        var db = Database(); db.EnsureCreated(); var clock = new Constructd.Fakes.MutableClock();
        IUserStore users = sqlite ? new SqliteUserStore(db) : new InMemoryUserStore();
        IVmRepository vms = sqlite ? new SqliteVmRepository(db) : new InMemoryVmRepository();
        await users.CreateAsync(new("alice", Role.User, 5, clock.UtcNow, true), Ct);
        await vms.AddAsync(Primary(), 5, Ct);
        ITokenService tokens = sqlite ? new SqliteTokenService(db, clock, users, vms) : new InMemoryTokenService(clock, users, vms);
        var issuer = (IVmTokenIssuer)tokens;
        var original = await issuer.IssueVmTokenAsync("parent", VmTokenKind.Legacy, Ct);
        var stale = (await vms.GetAsync("parent", Ct))!;
        var rotated = await issuer.IssueVmTokenAsync("parent", VmTokenKind.Primary, Ct);
        await vms.UpdateAsync(stale with { State = VmState.Running }, Ct);
        Assert.Null(await tokens.ValidateAsync(original, Ct)); Assert.NotNull(await tokens.ValidateAsync(rotated, Ct));
        Assert.True(await issuer.RevokeVmTokenAsync("parent", Ct));
        await vms.UpdateAsync(stale with { State = VmState.Off }, Ct);
        Assert.Null(await tokens.ValidateAsync(original, Ct)); Assert.Null(await tokens.ValidateAsync(rotated, Ct));
        Assert.True(await ((IVmDelegationRepository)vms).TryFenceAsync("parent", "deletion", true, Ct));
        await vms.UpdateAsync(stale with { State = VmState.Running, Deleting = false }, Ct);
        Assert.True((await vms.GetAsync("parent", Ct))!.Deleting);
        Assert.False(await ((IVmMetadataStore)vms).RestoreUnqueuedDeletionAsync(stale, Ct));
        Assert.Null((await vms.GetAsync("parent", Ct))!.VmTokenHash);
    }
    [Fact]
    public async Task PreFeatureDatabaseKeepsRowsAndHashesAndMigrationIsIdempotent()
    {
        var db = Database();
        using (var c = db.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Foundation", "PreFeatureSchema.sql")); cmd.ExecuteNonQuery();
            cmd.CommandText = """
                INSERT INTO users VALUES ('alice','User',3,'2026-01-01T00:00:00+00:00',1);
                INSERT INTO tokens VALUES ('token','alice','user-hash','2026-01-01T00:00:00+00:00',NULL,'old');
                INSERT INTO vms VALUES ('parent','alice',2,4,40,'2026-01-01T00:00:00+00:00','Running',2201,'existing-hash',120,'Save',0);
                INSERT INTO jobs VALUES ('job','create-vm','parent','alice','Succeeded','[]',NULL,NULL,'2026-01-01T00:00:00+00:00',NULL);
                INSERT INTO activity VALUES ('parent',1,'[]','2026-01-01T00:00:00+00:00');
                INSERT INTO audit(at,actor,action,target,outcome) VALUES ('2026-01-01','alice','old','parent','Success');
                INSERT INTO forwards(id,vm_name,vm_port,target,label,created) VALUES ('forward','parent',3000,'Client','old','2026-01-01');
                """; cmd.ExecuteNonQuery();
        }
        db.EnsureCreated(); db.EnsureCreated();
        using var check = db.Open(); using var query = check.CreateCommand();
        query.CommandText = "SELECT kind || ':' || vm_token_kind || ':' || vm_token_hash || ':' || ssh_forward_port FROM vms";
        Assert.Equal("primary:legacy:existing-hash:2201", query.ExecuteScalar());
        foreach (var table in new[] { "users", "tokens", "vms", "jobs", "activity", "audit", "forwards", "schema_migrations" })
        { query.CommandText = $"SELECT COUNT(*) FROM {table}"; Assert.Equal(table == "schema_migrations" ? 2L : 1L, query.ExecuteScalar()); }
        query.CommandText = "SELECT enabled || ':' || max_vms FROM users"; Assert.Equal("1:3", query.ExecuteScalar());
        var vm = (await new SqliteVmRepository(db).GetAsync("parent", Ct))!;
        Assert.Equal(VmKind.Primary, vm.Kind); Assert.Equal(VmTokenKind.Legacy, vm.TokenKind); Assert.Equal("existing-hash", vm.VmTokenHash);
        Assert.Null(vm.Lease); Assert.Null(vm.Hardware); Assert.Null(vm.Guest); Assert.Null(vm.Observed);
        var user = (await new SqliteUserStore(db).GetAsync("alice", Ct))!; Assert.True(user.Enabled); Assert.Equal(3, user.MaxVms);

    }

    [Fact]
    public void FailedMigrationRollsBackItsStatementsAndRecord()
    {
        var db = Database(); db.EnsureCreated(); using var c = db.Open();
        Assert.Throws<InvalidOperationException>(() => SqliteMigrationRunner.Apply(c, "test", [new FailingMigration()]));
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='failed_migration'";
        Assert.Equal(0L, cmd.ExecuteScalar());
        cmd.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE id=101"; Assert.Equal(0L, cmd.ExecuteScalar());
    }
    private sealed class FailingMigration : ISqliteMigration
    {
        public int Id => 101; public string Name => "failure"; public bool Breaking => false;
        public void Apply(SqliteConnection c, SqliteTransaction tx)
        { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "CREATE TABLE failed_migration(id INTEGER)"; cmd.ExecuteNonQuery(); throw new InvalidOperationException(); }
    }
    [Fact]
    public void MigrationRegistryIsOrderedUniqueAndInItsReservedRange()
    {
        var ids = SqliteMigrations.All.Select(m => m.Id).ToArray();
        Assert.Equal(ids.Order(), ids); Assert.Equal(ids.Length, ids.Distinct().Count()); Assert.All(ids, id => Assert.InRange(id, 100, 399));
        Assert.Equal(300, SqliteMigrations.SchemaVersion); Assert.Equal(0, SqliteMigrations.MinReadableBy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportsObservationsAndLeaseHaveIndependentAtomicWrites(bool sqlite)
    {
        var db = Database(); db.EnsureCreated();
        IVmRepository vms = sqlite ? new SqliteVmRepository(db) : new InMemoryVmRepository();
        var delegation = (IVmDelegationRepository)vms;
        await vms.AddAsync(Primary(), 10, Ct); await delegation.AddAsync(Child(), Allowance, Ct);
        var at = DateTimeOffset.UtcNow;
        await Task.WhenAll(delegation.UpdateGuestReportAsync("parent", new("abcdef0", at, null, at, GuestReportProvenance.Provisioner, null, null), Ct),
            delegation.UpdateGuestReportAsync("parent", new(null, null, at.AddDays(1), at, GuestReportProvenance.Provisioner, null, null), Ct));
        await delegation.UpdateGuestReportAsync("parent", new(null, null, null, null, GuestReportProvenance.Unknown, at.AddDays(2), "failed"), Ct);
        await delegation.UpdateObservationAsync("parent", new(at, at.AddDays(3), [], null), Ct);
        var vm = (await vms.GetAsync("parent", Ct))!;
        Assert.Equal("abcdef0", vm.Guest!.ConstructCommit); Assert.Equal(at, vm.Guest.ProvisionedAt); Assert.Equal(at.AddDays(1), vm.Guest.ReinstalledAt);
        Assert.Equal("failed", vm.Guest.LastAttemptOutcome); Assert.Equal(at.AddDays(3), vm.Observed!.LastBootAt);
        await delegation.UpdateObservationAsync("child", new(at, at, [], null), Ct);
        Assert.Null((await vms.GetAsync("child", Ct))!.Guest);
        Assert.False(await delegation.UpdateGuestReportAsync("child", vm.Guest, Ct));
        var lease = Child().Lease! with { Version = 1, ActivatedAt = at, ExpiresAt = at.AddHours(1), State = LeaseState.Active };
        var changes = await Task.WhenAll(delegation.UpdateLeaseAsync("child", lease, 0, Ct), delegation.UpdateLeaseAsync("child", lease, 0, Ct));
        Assert.Single(changes, x => x);
        Assert.Equal(VmAddOutcome.Added, await vms.AddAsync(Primary("second"), 2, Ct));
        Assert.Equal(VmAddOutcome.QuotaExceeded, await vms.AddAsync(Primary("third"), 2, Ct));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TokenRotationAndDisableAreImmediate(bool sqlite)
    {
        var db = Database(); db.EnsureCreated(); var clock = new MutableClock();
        IUserStore users = sqlite ? new SqliteUserStore(db) : new InMemoryUserStore();
        IVmRepository vms = sqlite ? new SqliteVmRepository(db) : new InMemoryVmRepository();
        ITokenService tokens = sqlite ? new SqliteTokenService(db, clock, users, vms) : new InMemoryTokenService(clock, users, vms);
        await users.CreateAsync(new("alice", Role.User, 2, clock.UtcNow), Ct); await vms.AddAsync(Primary(), 2, Ct);
        var old = await tokens.IssueVmTokenAsync("parent", Ct); var current = await ((IVmTokenIssuer)tokens).IssueVmTokenAsync("parent", VmTokenKind.Primary, Ct);
        Assert.Null(await tokens.ValidateAsync(old, Ct)); Assert.NotNull(await tokens.ValidateAsync(current, Ct));
        Assert.Equal(VmTokenKind.Primary, (await vms.GetAsync("parent", Ct))!.TokenKind);
        var bearer = await tokens.IssueAsync("alice", "test", Ct);
        await ((IUserAllowanceStore)users).SetEnabledAsync("alice", false, Ct);
        Assert.Null(await tokens.ValidateAsync(current, Ct)); Assert.Null(await tokens.ValidateAsync(bearer.Plaintext, Ct));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigCompareAndSetIsAtomicAndTimestampChangesEvenWithFixedClock(bool sqlite)
    {
        var db = Database(); db.EnsureCreated(); var clock = new MutableClock();
        IHostConfigStore store = sqlite ? new SqliteHostConfigStore(db, clock) : new InMemoryHostConfigStore(clock);
        var metadata = (IHostConfigMetadata)store;
        Assert.True(await store.TrySetAsync("network", new NetworkConfig(true, true), "admin", null, Ct));
        var first = (await metadata.ListSectionsAsync(Ct)).Single();
        Assert.True(await store.TrySetAsync("network", new NetworkConfig(false, true), "admin", first.UpdatedAt, Ct));
        Assert.False(await store.TrySetAsync("network", new NetworkConfig(true, true), "admin", first.UpdatedAt, Ct));
        Assert.False((await store.GetAsync<NetworkConfig>("network", Ct))!.HostForwardsEnabled);
    }
    [Fact]
    public async Task VmGateUsesCaseInsensitiveIdentityAndReleasesOnlyOnce()
    {
        var gate = new InMemoryVmOperationGate(); var handle = await gate.AcquireAsync("parent", "op", Ct);
        Assert.Null(await gate.TryAcquireAsync("PARENT", "other", Ct)); Assert.True(gate.IsHeld("PARENT", out var owner)); Assert.Equal("op", owner);
        await handle.DisposeAsync(); await handle.DisposeAsync(); await using var next = await gate.TryAcquireAsync("parent", "next", Ct); Assert.NotNull(next);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyUpdatePreservesMetadataAndIncarnationHasItsOwnWrite(bool sqlite)
    {
        var db = Database(); db.EnsureCreated();
        IVmRepository vms = sqlite ? new SqliteVmRepository(db) : new InMemoryVmRepository();
        await vms.AddAsync(Primary(), 2, Ct); var original = (await vms.GetAsync("parent", Ct))!;
        await ((IVmMetadataStore)vms).UpdateIncarnationAsync("parent", "actual-hypervisor-id", Ct);
        await ((IVmDelegationRepository)vms).UpdateGuestReportAsync("parent", new("abcdef0", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, GuestReportProvenance.Provisioner, null, null), Ct);
        await vms.UpdateAsync(original with { State = VmState.Running, TokenKind = VmTokenKind.Primary, Incarnation = "stale" }, Ct);
        var saved = (await vms.GetAsync("parent", Ct))!;
        Assert.Equal(VmState.Running, saved.State); Assert.Equal(VmTokenKind.Legacy, saved.TokenKind);
        Assert.Equal("actual-hypervisor-id", saved.Incarnation); Assert.Equal("abcdef0", saved.Guest!.ConstructCommit);
        IUserStore users = sqlite ? new SqliteUserStore(db) : new InMemoryUserStore();
        await users.CreateAsync(new("alice", Role.User, 2, DateTimeOffset.UtcNow), Ct);
        Assert.Equal(UserAllowance.Unset, (await users.GetAsync("alice", Ct))!.Allowance);
        var u = (await users.GetAsync("alice", Ct))!;
        await ((IUserAllowanceStore)users).SetAllowanceAsync("alice", UserAllowance.Unset with { MaxRetainedChildren = 5 }, Ct);
        await users.UpdateAsync(u with { MaxVms = 3 }, Ct);
        Assert.Equal(5, (await users.GetAsync("alice", Ct))!.Allowance!.MaxRetainedChildren);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CascadeAndFenceRespectLiveJobsAndMutableClock(bool sqlite)
    {
        var db = Database(); db.EnsureCreated(); var clock = new MutableClock();
        IJobStore jobs = sqlite ? new SqliteJobStore(db) : new InMemoryJobStore();
        IVmRepository vms = sqlite ? new SqliteVmRepository(db, clock) : new InMemoryVmRepository(jobs, clock);
        var store = (IVmDelegationRepository)vms;
        await vms.AddAsync(Primary(), 2, Ct); await store.AddAsync(Child(), Allowance, Ct);
        var job = new Job("busy", "child-create", "child", "alice", JobState.Running, [], null, null, clock.UtcNow, null);
        await jobs.UpsertAsync(job, Ct); Assert.True(await store.TryFenceAsync("child", "busy", false, Ct));
        var children = Constructd.Core.Logic.CascadeRules.Children(await store.ListChildrenAsync("parent", Ct));
        await store.SaveCascadePreviewAsync(new("parent", null, "preview", clock.UtcNow, clock.UtcNow.AddMinutes(1), children, CascadeState.Previewed, null, new Dictionary<string, string>()), Ct);
        var rejected = await store.TryAcceptCascadeAsync("parent", "preview", "cascade", Ct);
        Assert.False(rejected.Accepted); Assert.Equal("operation-in-progress", rejected.Reason);
        Assert.False((await vms.GetAsync("parent", Ct))!.Deleting); Assert.Equal("busy", (await vms.GetAsync("child", Ct))!.CurrentJobId);
        await jobs.UpsertAsync(job with { State = JobState.Failed }, Ct);
        Assert.True(await store.TryFenceAsync("child", "retry", false, Ct));
        // Missing/terminal jobs do not prevent a retry, and expiry uses the supplied clock.
        clock.Advance(TimeSpan.FromMinutes(2)); Assert.False((await store.TryAcceptCascadeAsync("parent", "preview", "cascade", Ct)).Accepted);
        await store.SaveCascadePreviewAsync(new("parent", null, "fresh", clock.UtcNow, clock.UtcNow.AddMinutes(1), children, CascadeState.Previewed, null, new Dictionary<string, string>()), Ct);
        Assert.True((await store.TryAcceptCascadeAsync("parent", "fresh", "cascade", Ct)).Accepted);
    }
    [Fact]
    public async Task IdleEngineSkipsChildren()
    {
        var vms = new InMemoryVmRepository(); await vms.AddAsync(Child(), 5, Ct);
        var engine = new IdlePolicyEngine(vms, null!, new FakeHypervisorDriver(), new InMemoryAuditLog(), new Constructd.Core.Configuration.IdleOptions());
        Assert.Empty(await engine.EvaluateAsync(DateTimeOffset.UtcNow, Ct));
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
