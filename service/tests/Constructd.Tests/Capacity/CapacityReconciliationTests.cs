using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Sqlite;
using static Constructd.Tests.Capacity.CapacityMathTests;

namespace Constructd.Tests.Capacity;

public sealed class CapacityReconciliationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "capacity-reconcile-" + Guid.NewGuid().ToString("n"));
    private readonly SqliteDatabase _database;
    private readonly SqliteVmRepository _vms;
    private readonly SqliteCapacityLedger _ledger;
    private readonly MutableClock _clock = new(Now);
    private readonly FakeHypervisorDriver _driver = new();
    private readonly FakeHypervisorInventory _inventory = new() { Snapshot = Inventory() };
    private readonly InMemoryOperationRegistry _operations = new();
    private readonly InMemoryVmOperationGate _gates = new();
    private readonly CapacityReconciler _reconciler;
    public CapacityReconciliationTests()
    {
        _database = new(Path.Combine(_directory, "test.db")); _database.EnsureCreated(); _vms = new(_database);
        _ledger = new(_database, _clock, _inventory, new ConstructdOptions()) { Operations = _operations };
        _reconciler = new(_inventory, _vms, _driver, _gates, _ledger);
    }
    private async Task<CapacityDecision> Setup(VmState state = VmState.Off, bool storage = false)
    {
        await _vms.AddAsync(Vm(), 10, default); _driver.SetState("a", state);
        _inventory.Snapshot = Inventory(28 * Gb, Actual(state: state));
        return await _ledger.TryReserveAsync(new("alice", "a", "op", storage ? [new(ReservationResource.Storage, 20 * Gb, "disk:C:\\a.vhdx", "C:\\")]
            : [new(ReservationResource.Ram, 8 * Gb, null, null), new(ReservationResource.Cpu, 2, null, null)], TimeSpan.FromMinutes(10)), default);
    }
    [Theory]
    [InlineData(ArtifactPresence.Absent, true, null, false)]
    [InlineData(ArtifactPresence.Present, true, null, false)]
    [InlineData(ArtifactPresence.Unknown, true, null, false)]
    [InlineData(ArtifactPresence.Absent, false, null, false)]
    [InlineData(ArtifactPresence.Absent, true, "create", false)]
    [InlineData(ArtifactPresence.Absent, true, null, true)]
    public async Task InterruptedAdmissionUsesArtifactEvidenceBeforeReleasingChildSlot(ArtifactPresence presence, bool complete, string? phase, bool partial)
    {
        await _vms.AddAsync(Vm("parent"), 10, default);
        await _vms.AddAsync(Vm() with { Kind = VmKind.Child, Parent = "parent", CurrentJobId = "interrupted", Incarnation = null }, 10, default);
        var jobs = new SqliteJobStore(_database);
        await jobs.UpsertAsync(new("interrupted", "child-create", "a", "alice", JobState.Queued, [], null, null, Now, null, Phase: phase), default);
        await jobs.MarkInterruptedAsync(Now, default);
        var artifact = @"disk:C:\a.vhdx";
        await _ledger.TryReserveAsync(new("alice", "a", "interrupted", [new(ReservationResource.Storage, 20 * Gb, artifact, @"C:\"),
            new(ReservationResource.Ram, Gb, null, null)], TimeSpan.FromMinutes(10)), default);
        if (partial)
            await new SqliteMediaStore(_database).AddAsync(new("pending", "alice", "aux.iso", MediaRole.Auxiliary, MediaSource.Upload,
                null, @"C:\media\aux.iso", MediaState.Pending, 10, 10, null, null, null, null, "a", Now, null, null), default);
        _inventory.Snapshot = Inventory() with { Complete = complete, Artifacts = [new(artifact, @"C:\a.vhdx", @"C:\", 0, presence)] };
        _driver.SetState("a", VmState.Absent);
        await _reconciler.ReconcileAsync(default);
        var removed = presence == ArtifactPresence.Absent && complete && phase is null && !partial;
        var current = await _vms.GetAsync("a", default);
        if (removed)
        {
            Assert.Null(current);
            Assert.DoesNotContain(await _ledger.ReadReservationsAsync(default), r => r.VmName == "a");
        }
        else
        {
            Assert.NotNull(current); Assert.Equal(phase is null, current.Deleting);
            Assert.Contains(await _ledger.ReadReservationsAsync(default), r => r.VmName == "a");
        }
        using var connection = _database.Open(); using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM audit WHERE action='vm.create.abandoned'";
        Assert.Equal(phase is null ? 1L : 0L, query.ExecuteScalar());
    }
    [Theory]
    [InlineData(ArtifactPresence.Absent)]
    [InlineData(ArtifactPresence.Present)]
    [InlineData(ArtifactPresence.Unknown)]
    public async Task FailedLaunchUsesTheSameAbandonedAdmissionRecovery(ArtifactPresence presence)
    {
        await _vms.AddAsync(Vm("parent"), 10, default);
        var vm = Vm() with { Kind = VmKind.Child, Parent = "parent", CurrentJobId = "not-launched", Incarnation = null, RamMb = 1024,
            Hardware = new(2, 1024, 20, 2, false, null, false, [], true),
            Lease = new("1h", 3600, null, null, LeaseState.Inactive, 0, null, null) };
        var job = new Job("not-launched", "child-create", "a", "alice", JobState.Queued, [], null, null, Now, null);
        var media = new MediaItem("ready", "alice", "install.iso", MediaRole.Install, MediaSource.Upload,
            null, @"C:\media\install.iso", MediaState.Ready, 10, 10, null, null, null, null, "a", Now, Now, null);
        var artifact = @"disk:C:\a.vhdx";
        var admission = new SqliteAdmissionStore(_ledger, _clock);
        var plan = new AdmissionPlan(null, vm, new(10, true, 10, null, null, null, null, true, true, true),
            [media], [], [new("ready", "a", MediaSlot.Install, Now)],
            new("alice", "a", job.Id, [new(ReservationResource.Storage, 20 * Gb, artifact, @"C:\"),
                new(ReservationResource.Ram, Gb, null, null)], TimeSpan.FromMinutes(10)), null, job, null, null, false);
        Assert.Equal(AdmissionOutcome.Accepted, (await admission.AdmitAsync(plan, default)).Outcome);
        await admission.MarkStartFailedAsync(job.Id, "launch failed", default);
        await new SqliteJobStore(_database).MarkInterruptedAsync(Now, default);
        _inventory.Snapshot = Inventory() with { Artifacts = [new(artifact, @"C:\a.vhdx", @"C:\", 0, presence)] };
        _driver.SetState("a", VmState.Absent);
        await _reconciler.ReconcileAsync(default);
        var references = await new SqliteMediaStore(_database).ListReferencesForVmAsync("a", default);
        if (presence == ArtifactPresence.Absent)
        {
            Assert.Null(await _vms.GetAsync("a", default)); Assert.Empty(references);
            Assert.DoesNotContain(await _ledger.ReadReservationsAsync(default), r => r.VmName == "a");
        }
        else
        {
            Assert.True((await _vms.GetAsync("a", default))!.Deleting); Assert.Single(references);
            Assert.All((await _ledger.ReadReservationsAsync(default)).Where(r => r.VmName == "a"), r => Assert.Equal(ReservationPhase.Held, r.Phase));
        }
        using var connection = _database.Open(); using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM audit WHERE action='vm.create.abandoned'";
        Assert.Equal(1L, query.ExecuteScalar());
    }
    [Theory]
    [InlineData("incarnation")]
    [InlineData("generation")]
    [InlineData("ambiguous")]
    public async Task UnresolvedPlacementCannotConsumeStaleOrAmbiguousIdentityEvidence(string conflict)
    {
        var vm = Vm() with { Incarnation = conflict == "ambiguous" ? null : "a-id" };
        await _vms.AddAsync(vm, 10, default);
        var reserved = await _ledger.TryReserveAsync(new("alice", "a", "create",
            [new(ReservationResource.Storage, 20 * Gb, "unresolved-primary-disk:a", "unknown")], TimeSpan.FromMinutes(1)), default);
        await _ledger.ConfirmAsync(reserved.ReservationIds, VmState.Running, default);
        var captured = await _ledger.ReadReservationsAsync(default);
        var actual = Actual(state: VmState.Running) with { Id = conflict == "incarnation" ? "other-id" : "a-id",
            Disks = [new(@"C:\a.vhdx", 20 * Gb, Gb, null, @"C:\", true)] };
        if (conflict == "generation")
        {
            using var c = _database.Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE vms SET power_generation=1 WHERE name='a'"; cmd.ExecuteNonQuery();
        }
        var evidence = conflict == "ambiguous" ? Inventory(28 * Gb, actual, actual with { Id = "duplicate-id" }) : Inventory(28 * Gb, actual);
        await _ledger.ApplyVmAsync(vm, VmState.Running, evidence, captured, default);
        Assert.Contains(await _ledger.ReadReservationsAsync(default), r => r.Artifact == "unresolved-primary-disk:a" && r.Amount == 20 * Gb);
    }
    [Theory]
    [InlineData(VmState.Off)] [InlineData(VmState.Saved)] [InlineData(VmState.Unknown)] [InlineData(VmState.Running)]
    public async Task LiveOperationKeepsPendingInEveryState(VmState state)
    {
        var accepted = await Setup(state); using var alive = _operations.Register("op", "start", "a"); _clock.Advance(TimeSpan.FromDays(1));
        await _reconciler.ReconcileAsync(default);
        var rows = await _ledger.ReadReservationsAsync(default);
        Assert.All(rows.Where(r => accepted.ReservationIds.Contains(r.Id)), r => Assert.Equal(ReservationPhase.Pending, r.Phase)); Assert.Equal(2, rows.Count);
    }
    [Fact]
    public async Task BusyVmGateIsSkippedWithoutWaiting()
    {
        await Setup(); await using var gate = await _gates.AcquireAsync("a", "restart", default); _clock.Advance(TimeSpan.FromDays(1));
        await _reconciler.ReconcileAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, (await _ledger.ReadReservationsAsync(default)).Count); Assert.Empty(_driver.Calls);
    }
    [Fact]
    public async Task OrphanBeforeDeadlineKeptThenReleasedOnOff()
    {
        await Setup(); await _reconciler.ReconcileAsync(default); Assert.Equal(2, (await _ledger.ReadReservationsAsync(default)).Count);
        _clock.Advance(TimeSpan.FromMinutes(11)); await _reconciler.ReconcileAsync(default); Assert.Empty(await _ledger.ReadReservationsAsync(default));
    }
    [Theory]
    [InlineData(VmState.Running)] [InlineData(VmState.Unknown)] [InlineData(VmState.Paused)]
    public async Task OrphanActivePromotesEvenBeforeDeadline(VmState state)
    {
        var accepted = await Setup(state); await _reconciler.ReconcileAsync(default);
        var rows = (await _ledger.ReadReservationsAsync(default)).Where(r => accepted.ReservationIds.Contains(r.Id)).ToArray();
        Assert.Equal(2, rows.Length); Assert.All(rows, r => Assert.Equal(ReservationPhase.Held, r.Phase));
    }
    [Theory]
    [InlineData(ArtifactPresence.Present, ReservationPhase.Held, 1)]
    [InlineData(ArtifactPresence.Absent, ReservationPhase.Pending, 0)]
    [InlineData(ArtifactPresence.Unknown, ReservationPhase.Pending, 1)]
    public async Task RetainedOrphanDisksNeedFileEvidence(ArtifactPresence presence, ReservationPhase phase, int count)
    {
        await Setup(storage: true); _clock.Advance(TimeSpan.FromMinutes(11));
        _inventory.Snapshot = _inventory.Snapshot with { Artifacts = [new("disk:C:\\a.vhdx", "C:\\a.vhdx", "C:\\", 1, presence)] };
        await _reconciler.ReconcileAsync(default); var rows = await _ledger.ReadReservationsAsync(default);
        Assert.Equal(count, rows.Count); if (count > 0) Assert.Equal(phase, rows[0].Phase);
    }
    [Fact]
    public async Task StaleOffSnapshotCannotReleaseACompletedStart()
    {
        var accepted = await Setup(); var before = (await _vms.GetAsync("a", default))!; var captured = await _ledger.ReadReservationsAsync(default);
        var stale = _inventory.Snapshot;
        await _ledger.ConfirmAsync(accepted.ReservationIds, VmState.Running, default);
        using (var c = _database.Open()) { using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE vms SET state='Running',power_generation=1 WHERE name='a'"; cmd.ExecuteNonQuery(); }
        await _ledger.ApplyVmAsync(before, VmState.Off, stale, captured, default);
        Assert.Equal(2, (await _ledger.ReadReservationsAsync(default)).Count); Assert.Equal(1, (await _vms.GetAsync("a", default))!.PowerGeneration);
    }
    [Fact]
    public async Task ReconciliationUsesFreshStateAfterInventory()
    {
        var accepted = await Setup(); await _ledger.ConfirmAsync(accepted.ReservationIds, VmState.Running, default);
        _driver.SetState("a", VmState.Running); // Inventory still says Off.
        await _reconciler.ReconcileAsync(default);
        Assert.Equal(3, (await _ledger.ReadReservationsAsync(default)).Count); Assert.Equal(VmState.Running, (await _vms.GetAsync("a", default))!.State);
    }
    [Fact]
    public async Task ExternalStartIsChargedToOwnerAndUnmanagedHasNoOwner()
    {
        await _vms.AddAsync(Vm(), 10, default); _driver.SetState("a", VmState.Running);
        _inventory.Snapshot = Inventory(12 * Gb, Actual(state: VmState.Running, assigned: 8 * Gb), Actual("outside", VmState.Running, 8 * Gb));
        await _reconciler.ReconcileAsync(default);
        Assert.All(await _ledger.ReadReservationsAsync(default), r => Assert.Equal("alice", r.ScopeOwner));
        var snapshot = await _ledger.SnapshotAsync(true, default); Assert.Single(snapshot.Unmanaged); Assert.Equal(8 * Gb, snapshot.RamUnmanagedBytes);
        Assert.Equal(8 * Gb, snapshot.RamReservedBytes);
    }
    [Fact]
    public async Task SavedConfirmationReleasesRuntimeButKeepsSavedStorage()
    {
        var accepted = await Setup(VmState.Running); await _ledger.ConfirmAsync(accepted.ReservationIds, VmState.Running, default);
        await _reconciler.ReconcileAsync(default); _driver.SetState("a", VmState.Saved);
        _inventory.Snapshot = Inventory(28 * Gb, Actual(state: VmState.Saved) with { SavedStateBytes = Gb });
        await _reconciler.ReconcileAsync(default);
        var row = Assert.Single(await _ledger.ReadReservationsAsync(default)); Assert.Equal(ReservationResource.Storage, row.Resource);
        Assert.Equal(8 * Gb + Constructd.Core.Logic.CapacityMath.SavedStateOverhead, row.Amount);
    }
    [Fact]
    public async Task ReusedNameNeverReleasesOldIncarnationsHold()
    {
        var accepted = await Setup(); await _ledger.ConfirmAsync(accepted.ReservationIds, VmState.Running, default);
        _inventory.Snapshot = Inventory(28 * Gb, Actual() with { Id = "different-id" }); _driver.SetState("a", VmState.Off);
        await _reconciler.ReconcileAsync(default); Assert.Equal(2, (await _ledger.ReadReservationsAsync(default)).Count);
        Assert.Single((await _ledger.SnapshotAsync(true, default)).Unmanaged);
    }
    [Fact]
    public async Task ThreeVmPassReadsInventoryExactlyOnce()
    {
        foreach (var name in new[] { "a", "b", "c" })
        { await _vms.AddAsync(Vm(name), 10, default); _driver.SetState(name, VmState.Running); }
        _inventory.Snapshot = Inventory(4 * Gb, Actual("a", VmState.Running), Actual("b", VmState.Running), Actual("c", VmState.Running));
        var before = _inventory.Reads;
        await _reconciler.ReconcileAsync(default);
        Assert.Equal(before + 1, _inventory.Reads);
        Assert.Equal(9, (await _ledger.ReadReservationsAsync(default)).Count);
        await _ledger.SnapshotAsync(false, default); Assert.Equal(before + 1, _inventory.Reads);
    }
    [Fact]
    public async Task UnrelatedInventoryFailureDoesNotInventRuntimeForOffVm()
    {
        await _vms.AddAsync(Vm(), 10, default); _driver.SetState("a", VmState.Off);
        _inventory.Snapshot = Inventory(28 * Gb, Actual(), Actual("broken") with { Complete = false }) with
            { Complete = false, Problems = ["disk-unreadable"] };
        await _reconciler.ReconcileAsync(default);
        Assert.Empty(await _ledger.ReadReservationsAsync(default));
        var snapshot = await _ledger.SnapshotAsync(false, default); Assert.False(snapshot.Complete); Assert.Contains("disk-unreadable", snapshot.Problems!);
    }
    [Theory]
    [InlineData(VmState.Running, ArtifactPresence.Absent, 0)]
    [InlineData(VmState.Saved, ArtifactPresence.Present, 1)]
    public async Task SavedStateGrowthDoesNotBlockSubsequentAdmission(VmState state, ArtifactPresence presence, int allocatedGb)
    {
        await new SqliteHostConfigStore(_database, _clock).SetAsync("capacity", Config, "admin", default);
        await _vms.AddAsync(Vm(), 10, default); _driver.SetState("a", state);
        _inventory.Snapshot = Inventory(20 * Gb, Actual(state: state, assigned: state == VmState.Running ? 8 * Gb : 0) with { SavedStateBytes = allocatedGb * Gb });
        await _reconciler.ReconcileAsync(default);
        _inventory.Snapshot = _inventory.Snapshot with { Artifacts = [new("saved-state:a-id", "C:\\a.vmrs", "C:\\", allocatedGb * Gb, presence)] };
        var snapshot = await _ledger.SnapshotAsync(true, default);
        Assert.True(snapshot.Complete); Assert.Empty(snapshot.Problems!);
        Assert.Equal(8 * Gb + Constructd.Core.Logic.CapacityMath.SavedStateOverhead - allocatedGb * Gb, snapshot.Volumes[0].GrowthReservedBytes);
        var decision = await _ledger.TryReserveAsync(new("bob", "next-vm", "next-op", [new(ReservationResource.Ram, Gb, null, null)], TimeSpan.FromMinutes(10)), default);
        Assert.True(decision.Allowed);
        await _reconciler.ReconcileAsync(default);
        Assert.Null((await _vms.GetAsync("a", default))!.Observed?.StorageProblem);
    }
    [Fact]
    public async Task PreIncarnationSavedStateHoldSurvivesReconciliationWithoutDuplication()
    {
        await _vms.AddAsync(Vm(), 10, default); _driver.SetState("a", VmState.Running);
        _inventory.Snapshot = Inventory(20 * Gb, Actual(state: VmState.Running, assigned: 8 * Gb));
        var accepted = await _ledger.TryReserveAsync(new("alice", "a", "create", [
            new(ReservationResource.Storage, 8 * Gb + Constructd.Core.Logic.CapacityMath.SavedStateOverhead, "saved-state:a", "C:\\")], TimeSpan.FromMinutes(10)), default);
        Assert.True(accepted.Allowed);
        await _ledger.ConfirmAsync(accepted.ReservationIds, VmState.Running, default);
        await _reconciler.ReconcileAsync(default);
        var saved = (await _ledger.ReadReservationsAsync(default)).Where(Constructd.Core.Logic.ReservationRules.SavedState).ToArray();
        Assert.Equal(accepted.ReservationIds[0], Assert.Single(saved).Id);
        _driver.SetState("a", VmState.Saved);
        _inventory.Snapshot = Inventory(28 * Gb, Actual(state: VmState.Saved) with { SavedStateBytes = 8 * Gb });
        await _reconciler.ReconcileAsync(default);
        Assert.Equal(accepted.ReservationIds[0], Assert.Single(await _ledger.ReadReservationsAsync(default)).Id);
        var snapshot = await _ledger.SnapshotAsync(true, default);
        Assert.True(snapshot.Complete);
        Assert.Equal(Constructd.Core.Logic.CapacityMath.SavedStateOverhead, snapshot.Volumes[0].GrowthReservedBytes);
    }
    [Fact]
    public async Task SavedVmWithMissingVmrsFailsClosed()
    {
        await _vms.AddAsync(Vm(), 10, default); _driver.SetState("a", VmState.Saved);
        _inventory.Snapshot = Inventory(28 * Gb, Actual(state: VmState.Saved) with { SavedStateBytes = 0 });
        await _reconciler.ReconcileAsync(default);
        var snapshot = await _ledger.SnapshotAsync(true, default);
        Assert.False(snapshot.Complete); Assert.Contains("artifact-unreadable-or-missing", snapshot.Problems!);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }
}
