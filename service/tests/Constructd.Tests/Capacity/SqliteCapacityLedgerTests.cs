using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Sqlite;
using static Constructd.Tests.Capacity.CapacityMathTests;

namespace Constructd.Tests.Capacity;

public sealed class SqliteCapacityLedgerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "construct-capacity-" + Guid.NewGuid().ToString("n"));
    private readonly MutableClock _clock = new(Now);
    private readonly FakeHypervisorInventory _inventory = new() { Snapshot = Inventory(5 * Gb) };
    private readonly SqliteDatabase _database;
    private readonly ConstructdOptions _options = new();
    private readonly SqliteCapacityLedger _ledger;
    public SqliteCapacityLedgerTests()
    {
        _database = new(Path.Combine(_directory, "test.db")); _database.EnsureCreated();
        _options.HostAdmin.Capacity.Mode = CapacityMode.Enforce;
        _ledger = new(_database, _clock, _inventory, _options);
    }
    private static ReservationRequest Request(string vm, long ram = Gb, params ReservationLine[] extra) =>
        new("alice", vm, "start-" + vm, [new(ReservationResource.Ram, ram, null, null), .. extra], TimeSpan.FromMinutes(10));
    [Fact]
    public async Task ParallelStartsOnlyOneGetsLastGb()
    {
        var decisions = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => _ledger.TryReserveAsync(Request("vm" + i), default))));
        Assert.Single(decisions, d => d.Allowed); Assert.Equal(15, decisions.Count(d => !d.Allowed));
        var refused = decisions.First(d => !d.Allowed); Assert.Equal(Gb, refused.Requested); Assert.Equal(0, refused.Available); Assert.Equal("ram", refused.Resource);
        Assert.Single((await _ledger.SnapshotAsync(false, default)).Reservations);
    }
    [Fact]
    public async Task ParallelCreatesReserveDiskAndRuntimeTogether()
    {
        _inventory.Snapshot = Inventory(28 * Gb) with { Host = Inventory().Host with { Volumes = [new("C:\\", 100 * Gb, 21 * Gb)] } };
        var decisions = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => _ledger.TryReserveAsync(Request("vm" + i, Gb,
            new ReservationLine(ReservationResource.Storage, Gb, "disk:C:\\vm" + i + ".vhdx", "C:\\")), default))));
        Assert.Single(decisions, d => d.Allowed); Assert.Equal(2, (await _ledger.SnapshotAsync(false, default)).Reservations.Count);
    }
    [Fact]
    public async Task FailedStartRestoresPreviousAccountingOnlyAfterOff()
    {
        var accepted = await _ledger.TryReserveAsync(Request("a"), default);
        await _ledger.ReleaseAsync(accepted.ReservationIds, VmState.Unknown, "timeout", default);
        Assert.False((await _ledger.TryReserveAsync(Request("b"), default)).Allowed);
        await _ledger.ReleaseAsync(accepted.ReservationIds, VmState.Off, "failed-start", default);
        Assert.True((await _ledger.TryReserveAsync(Request("b"), default)).Allowed);
    }
    [Fact]
    public async Task ConfirmedRunningHoldSurvivesShutdownRequestAndSavedKeepsDisk()
    {
        _inventory.Snapshot = Inventory();
        var accepted = await _ledger.TryReserveAsync(Request("a", Gb, new ReservationLine(ReservationResource.Storage, Gb, "disk:C:\\a.vhdx", "C:\\")), default);
        await _ledger.ConfirmAsync(accepted.ReservationIds, VmState.Running, default);
        await _ledger.ReleaseAsync(accepted.ReservationIds, VmState.Running, "shutdown-requested", default);
        Assert.Equal(2, (await _ledger.SnapshotAsync(false, default)).Reservations.Count);
        await _ledger.ReleaseAsync(accepted.ReservationIds, VmState.Saved, "saved", default);
        Assert.Equal(ReservationResource.Storage, Assert.Single((await _ledger.SnapshotAsync(false, default)).Reservations).Resource);
    }
    [Fact]
    public async Task RestartPreservesPersistedReservations()
    {
        var accepted = await _ledger.TryReserveAsync(Request("a"), default);
        var restarted = new SqliteCapacityLedger(_database, _clock, _inventory, _options);
        Assert.Equal(accepted.ReservationIds[0], Assert.Single((await restarted.SnapshotAsync(false, default)).Reservations).Id);
        Assert.False((await restarted.TryReserveAsync(Request("b"), default)).Allowed);
    }
    [Fact]
    public async Task EnforceFailsClosedObserveRecordsWouldRefuse()
    {
        _inventory.Snapshot = Inventory() with { Complete = false, Problems = ["unreadable-disk"] };
        Assert.False((await _ledger.TryReserveAsync(Request("a"), default)).Allowed);
        _options.HostAdmin.Capacity.Mode = CapacityMode.Observe;
        Assert.Equal("observe:inventory-incomplete", (await _ledger.TryReserveAsync(Request("a"), default)).Reason);
        Assert.Contains(await new SqliteAuditLog(_database).QueryAsync(10, default), a => a.Action == "capacity.observe");
    }
    [Fact]
    public async Task OwnerBudgetAggregatesMultiplePrimaries()
    {
        _inventory.Snapshot = Inventory();
        var users = new SqliteUserStore(_database);
        await users.CreateAsync(new("alice", Role.User, 3, Now, true, Allowance: UserAllowance.Unset with { RamBudgetBytes = Gb }), default);
        Assert.True((await _ledger.TryReserveAsync(Request("primary-a"), default)).Allowed);
        var denied = await _ledger.TryReserveAsync(Request("primary-b"), default);
        Assert.False(denied.Allowed); Assert.Equal("user", denied.Scope); Assert.Equal(Gb, denied.AllowedAmount);
    }
    [Fact]
    public async Task PlanRollbackRemovesReservationAndAuditTogether()
    {
        await using (var tx = await _ledger.BeginAsync(default))
        {
            Assert.True(tx.ReserveInTransaction(Request("a")).Allowed);
            using var command = tx.Connection.CreateCommand(); command.Transaction = tx.Sql;
            command.CommandText = "INSERT INTO jobs(id,kind,owner,state,progress,created) VALUES('job','create-vm','alice','Queued','[]','2026-09-07')";
            command.ExecuteNonQuery();
            // Simulate a later plan check failing: dispose without commit.
        }
        Assert.Empty((await _ledger.SnapshotAsync(false, default)).Reservations);
        Assert.Empty(await new SqliteAuditLog(_database).QueryAsync(10, default));
        using var c = _database.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM jobs"; Assert.Equal(0L, cmd.ExecuteScalar());
    }
    [Fact]
    public async Task OperationReplayIsIdempotentAndConflictingStartIsRefused()
    {
        var one = await _ledger.TryReserveAsync(Request("a"), default);
        var two = await _ledger.TryReserveAsync(Request("a"), default);
        Assert.Equal(one.ReservationIds, two.ReservationIds);
        Assert.False((await _ledger.TryReserveAsync(Request("a") with { OperationId = "other" }, default)).Allowed);
    }
    [Fact]
    public async Task TrimInvalidatesEpochAndUsesPhysicalFileSizeOnce()
    {
        _inventory.Snapshot = Inventory();
        var request = new ReservationRequest("alice", null, "upload", [new(ReservationResource.Storage, 20 * Gb, "media:123", "C:\\")], TimeSpan.FromMinutes(10));
        var accepted = await _ledger.TryReserveAsync(request, default);
        _inventory.Snapshot = Inventory() with { Epoch = 8, Host = Inventory().Host with { Volumes = [new("C:\\", 100 * Gb, 70 * Gb)] },
            Artifacts = [new("media:123", "C:\\media\\123.iso", "C:\\", 10 * Gb, ArtifactPresence.Present)] };
        await _ledger.TrimAsync(accepted.ReservationIds[0], 10 * Gb, default);
        var snapshot = await _ledger.SnapshotAsync(true, default);
        Assert.Equal(8, snapshot.Epoch); Assert.Equal(0, snapshot.Volumes[0].GrowthReservedBytes); Assert.Equal(50 * Gb, snapshot.Volumes[0].AvailableBytes);
    }
    [Fact]
    public async Task CachedReportingAndMutationNeverInvokeInventoryAfterWriteOrExpiry()
    {
        await _ledger.TryReserveAsync(Request("a"), default);
        var reads = _inventory.Reads;
        _clock.Advance(TimeSpan.FromDays(1));
        var snapshot = await _ledger.SnapshotAsync(false, default);
        await _ledger.ConfirmAsync(snapshot.Reservations.Select(r => r.Id).ToArray(), VmState.Running, default);
        await _ledger.SnapshotAsync(false, default);
        Assert.Equal(reads, _inventory.Reads);
        await _ledger.SnapshotAsync(true, default); Assert.Equal(reads + 1, _inventory.Reads);
    }
    [Fact]
    public async Task UnprimedReportingDoesNotSpawnInventory()
    {
        var snapshot = await _ledger.SnapshotAsync(false, default);
        Assert.False(snapshot.Complete); Assert.Equal(0, _inventory.Reads);
    }
    [Fact]
    public async Task SharedStartCannotChargeCallerInsteadOfVmOwner()
    {
        await new SqliteVmRepository(_database).AddAsync(Vm(), 10, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _ledger.TryReserveAsync(Request("a") with { Owner = "bob" }, default));
        Assert.Empty(await _ledger.ReadReservationsAsync(default));
        var accepted = await _ledger.TryReserveAsync(Request("a"), default);
        Assert.True(accepted.Allowed);
        Assert.Equal("alice", Assert.Single(await _ledger.ReadReservationsAsync(default)).ScopeOwner);
    }
    [Theory]
    [InlineData(CapacityMode.Observe, true, "capacity.observe")]
    [InlineData(CapacityMode.Enforce, false, "capacity.refuse")]
    public async Task ConflictingRuntimeReservationRespectsModeAndIsAudited(CapacityMode mode, bool allowed, string audit)
    {
        _options.HostAdmin.Capacity.Mode = mode;
        await _ledger.TryReserveAsync(Request("a"), default);
        var decision = await _ledger.TryReserveAsync(Request("a") with { OperationId = "second" }, default);
        Assert.Equal(allowed, decision.Allowed); Assert.Null(decision.Resource); Assert.Null(decision.Scope);
        Assert.Equal(mode == CapacityMode.Observe ? "observe:operation-in-progress" : "operation-in-progress", decision.Reason);
        Assert.Contains(await new SqliteAuditLog(_database).QueryAsync(10, default), a => a.Action == audit && a.Detail == decision.Reason);
    }
    [Fact]
    public async Task UserBudgetIsReportedBeforeIncompleteInventory()
    {
        await new SqliteUserStore(_database).CreateAsync(new("alice", Role.User, 3, Now, true, Allowance: UserAllowance.Unset with { RamBudgetBytes = 0 }), default);
        _inventory.Snapshot = Inventory() with { Complete = false };
        var decision = await _ledger.TryReserveAsync(Request("a"), default);
        Assert.Equal("user-budget", decision.Reason); Assert.Equal("ram", decision.Resource); Assert.Equal("user", decision.Scope);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }
}
