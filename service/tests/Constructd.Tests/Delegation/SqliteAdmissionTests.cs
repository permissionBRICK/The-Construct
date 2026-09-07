using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Sqlite;
using Microsoft.Data.Sqlite;

namespace Constructd.Tests.Delegation;

public sealed class SqliteAdmissionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "delegation-admission-" + Guid.NewGuid().ToString("n"));
    private readonly MutableClock clock = new(DateTimeOffset.Parse("2026-09-07T00:00:00Z"));
    private readonly SqliteDatabase db;
    private readonly SqliteVmRepository vms;
    private readonly SqliteCapacityLedger ledger;
    private readonly SqliteAdmissionStore admission;
    private readonly EffectiveAllowance allowance = new(5, true, 5, null, null, null, null, true, true, true);
    public SqliteAdmissionTests()
    {
        db = new(Path.Combine(root, "test.db")); db.EnsureCreated();
        vms = new(db, clock);
        ledger = new(db, clock, new FakeHypervisorInventory(), new ConstructdOptions());
        admission = new(ledger, clock);
    }
    private Vm Vm(string name) => new(name, "alice", 1, 1, 8, clock.UtcNow, VmState.Off, null, null, IdlePolicy.Disabled, []);
    private OperationKeyRecord Key(string target = "vm") => new("alice", "test", "operation-key", "fingerprint", target, null, OperationKeyState.InFlight, null, 0, null, clock.UtcNow);
    private AdmissionPlan Plan(Vm vm) => new(Key(vm.Name), vm, allowance, [], [], [],
        new("alice", vm.Name, "job", [new(ReservationResource.Ram, 1024, null, null)], TimeSpan.FromMinutes(10)),
        null, new("job", "test", vm.Name, "alice", JobState.Queued, [], null, null, clock.UtcNow, null), null, null, false);
    [Fact]
    public async Task AdmissionPersistsAllRowsAndReplaysWithoutDuplicateLiability()
    {
        Assert.Equal(AdmissionOutcome.Accepted, (await admission.AdmitAsync(Plan(Vm("vm")), default)).Outcome);
        Assert.Equal(AdmissionOutcome.Replay, (await admission.AdmitAsync(Plan(Vm("vm")), default)).Outcome);
        Assert.NotNull(await new SqliteVmRepository(db).GetAsync("vm", default));
        Assert.NotNull(await new SqliteJobStore(db).GetAsync("job", default));
        Assert.Single((await ledger.SnapshotAsync(false, default)).Reservations);
    }
    [Fact]
    public async Task LateFailureRollsBackVmReservationKeyAndAudit()
    {
        var plan = Plan(Vm("vm")) with { JobToInsert = Plan(Vm("vm")).JobToInsert! with { State = JobState.Running } };
        Assert.Equal(AdmissionOutcome.VersionConflict, (await admission.AdmitAsync(plan, default)).Outcome);
        Assert.Null(await vms.GetAsync("vm", default));
        Assert.Null(await new SqliteOperationKeyStore(db).GetAsync("alice", "test", "operation-key", default));
        Assert.Empty((await ledger.SnapshotAsync(false, default)).Reservations);
        Assert.Empty(await new SqliteAuditLog(db).QueryAsync(100, default));
    }
    [Fact]
    public async Task FalseCasRollsBackEarlierSuccessfulMutationEvenIfDelegateReturnsTrue()
    {
        await vms.AddAsync(Vm("vm"), allowance, default);
        var result = await admission.MutateAsync(Key(), async scope =>
        {
            Assert.True(await scope.BumpPowerGenerationAsync("vm", 0));
            Assert.False(await scope.BumpPowerGenerationAsync("vm", 0));
            return true;
        }, default);
        Assert.Equal(AdmissionOutcome.VersionConflict, result.Outcome);
        Assert.Equal(0, (await vms.GetAsync("vm", default))!.PowerGeneration);
        Assert.Null(await new SqliteOperationKeyStore(db).GetAsync("alice", "test", "operation-key", default));
    }
    [Fact]
    public async Task CompletedMutationReplaysAndSurvivesStoreRecreation()
    {
        await vms.AddAsync(Vm("vm"), allowance, default);
        Assert.Equal(AdmissionOutcome.Accepted, (await admission.MutateAsync(Key(), async scope =>
        {
            await scope.BumpPowerGenerationAsync("vm", 0);
            return await scope.CompleteOperationKeyAsync("alice", "test", "operation-key", "{}");
        }, default)).Outcome);
        var restarted = new SqliteAdmissionStore(new SqliteCapacityLedger(new SqliteDatabase(Path.Combine(root, "test.db")), clock, new FakeHypervisorInventory(), new ConstructdOptions()), clock);
        Assert.Equal(AdmissionOutcome.Replay, (await restarted.MutateAsync(Key(), _ => throw new Exception("must not execute"), default)).Outcome);
        Assert.Equal(1, (await vms.GetAsync("vm", default))!.PowerGeneration);
    }
    [Fact]
    public async Task ConflictingKeyNeverExecutesMutation()
    {
        await admission.MutateAsync(Key(), _ => Task.FromResult(true), default);
        Assert.Equal(AdmissionOutcome.KeyConflict, (await admission.MutateAsync(Key() with { Fingerprint = "different" }, _ => throw new Exception("must not execute"), default)).Outcome);
    }
    [Fact]
    public async Task FailedRunnerRetainsFenceAndCapacityAndStoresOnlyFixedError()
    {
        await admission.AdmitAsync(Plan(Vm("vm")), default);
        await admission.MarkStartFailedAsync("job", "untrusted dependency detail", default);
        Assert.Equal(JobState.Failed, (await new SqliteJobStore(db).GetAsync("job", default))!.State);
        Assert.NotNull(await vms.GetAsync("vm", default));
        Assert.Single((await ledger.SnapshotAsync(false, default)).Reservations);
        Assert.DoesNotContain("untrusted", (await new SqliteJobStore(db).GetAsync("job", default))!.Error!);
    }
    private Vm Child(string name = "child") => Vm(name) with { Kind = VmKind.Child, Parent = "parent", RamMb = 512,
        Hardware = new(1, 512, 8, 2, false, null, false, [], true), Lease = new("10m", 600, null, null, LeaseState.Inactive, 0, null, null) };
    private AdmissionPlan Empty() => new(null, null, null, [], [], [], null, null, null, null, null, false);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CascadeAcceptsExactScopeAndRollsBackMismatch(bool changed)
    {
        await vms.AddAsync(Vm("parent") with { VmTokenHash = "hash" }, allowance, default);
        await vms.AddAsync(Child(), allowance, default);
        var preview = new CascadePreview("parent", null, "token", clock.UtcNow, clock.UtcNow.AddMinutes(10),
            Constructd.Core.Logic.CascadeRules.Children(await vms.ListChildrenAsync("parent", default)), CascadeState.Previewed, null, new Dictionary<string,string>());
        await vms.SaveCascadePreviewAsync(preview, default);
        if (changed) await vms.AddAsync(Child("new-child"), allowance, default);
        var result = await admission.AdmitAsync(Empty() with { OperationKey = Key("parent"), CascadeToAccept = preview,
            FenceJobId = "job", JobToInsert = Plan(Vm("parent")).JobToInsert }, default);
        Assert.Equal(changed ? AdmissionOutcome.CascadeMismatch : AdmissionOutcome.Accepted, result.Outcome);
        var parent = (await vms.GetAsync("parent", default))!;
        Assert.Equal(!changed, parent.Deleting); Assert.Equal(!changed, parent.ChildCreationClosed);
        Assert.Equal(changed ? "hash" : null, parent.VmTokenHash);
        foreach (var child in await vms.ListChildrenAsync("parent", default))
        { Assert.Equal(!changed, child.Deleting); Assert.Equal(changed ? null : "job", child.CurrentJobId); }
        Assert.Equal(changed ? CascadeState.Previewed : CascadeState.Accepted, (await vms.GetCascadePreviewAsync("parent", default))!.State);
        Assert.Equal(changed, await new SqliteJobStore(db).GetAsync("job", default) is null);
        Assert.Equal(changed, await new SqliteOperationKeyStore(db).GetAsync("alice", "test", "operation-key", default) is null);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FenceRejectsMissingJobAndLiveJobWithoutWriting(bool live)
    {
        await vms.AddAsync(Vm("vm") with { CurrentJobId = live ? "live-job" : null }, allowance, default);
        if (live) await new SqliteJobStore(db).UpsertAsync(Plan(Vm("vm")).JobToInsert! with { Id = "live-job" }, default);
        var result = await admission.AdmitAsync(Empty() with { VmToFence = "vm", FenceJobId = live ? "job" : null,
            OperationKey = Key(), JobToInsert = Plan(Vm("vm")).JobToInsert }, default);
        Assert.Equal(AdmissionOutcome.VersionConflict, result.Outcome);
        Assert.False((await vms.GetAsync("vm", default))!.Deleting);
        Assert.Null(await new SqliteJobStore(db).GetAsync("job", default));
        Assert.Null(await new SqliteOperationKeyStore(db).GetAsync("alice", "test", "operation-key", default));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MediaOrCapacityRefusalRollsBackEarlierRows(bool capacityRefusal)
    {
        var media = new MediaItem("media", "alice", "test.iso", MediaRole.Install, MediaSource.Upload, null, "path",
            capacityRefusal ? MediaState.Ready : MediaState.Pending, 10, 10, null, null, null, null, null, clock.UtcNow, null, null);
        var config = new SqliteHostConfigStore(db, clock);
        if (capacityRefusal) await config.SetAsync("capacity", Constructd.Core.Configuration.HostAdminDefaults.Capacity with { Mode = CapacityMode.Enforce }, "admin", default);
        var plan = Plan(Vm("vm")) with { MediaToInsert = [media], ReferencesToInsert = [new("media", "vm", MediaSlot.Install, clock.UtcNow)] };
        if (capacityRefusal) plan = plan with { Reservation = plan.Reservation! with { Lines = [new(ReservationResource.Ram, 1L << 50, null, null)] } };
        Assert.Equal(capacityRefusal ? AdmissionOutcome.CapacityRefused : AdmissionOutcome.MediaNotReady, (await admission.AdmitAsync(plan, default)).Outcome);
        Assert.Null(await vms.GetAsync("vm", default));
        Assert.Empty(await new SqliteMediaStore(db).ListAsync(null, default));
        Assert.Null(await new SqliteOperationKeyStore(db).GetAsync("alice", "test", "operation-key", default));
        Assert.Empty((await ledger.SnapshotAsync(false, default)).Reservations);
    }
    [Fact]
    public async Task ScopeWritesReadBackAndLeaseCasRollsBackAudit()
    {
        await new SqliteUserStore(db).CreateAsync(new("alice", Role.User, 5, clock.UtcNow, true), default);
        await vms.AddAsync(Vm("parent"), allowance, default); await vms.AddAsync(Child(), allowance, default);
        var key = Key("child");
        await new SqliteOperationKeyStore(db).TryInsertAsync(key, default);
        var lease = Child().Lease! with { Version = 1, State = LeaseState.Active, ActivatedAt = clock.UtcNow, ExpiresAt = clock.UtcNow.AddMinutes(10) };
        Assert.Equal(AdmissionOutcome.Accepted, (await admission.MutateAsync(key, async scope =>
        {
            Assert.Equal("child", (await scope.ReadVmAsync("child"))!.Name);
            await scope.UpdateSharingAsync("child", SharingScope.Host);
            await scope.UpdateLeaseAsync("child", lease, 0);
            await scope.SetAllowanceAsync("alice", UserAllowance.Unset with { MaxRetainedChildren = 7 });
            await scope.SetOverrideAsync(new("parent", false, 2, 600, false, false, clock.UtcNow));
            await scope.AppendAuditAsync(new(clock.UtcNow, "alice", "test", "child", AuditOutcome.Success, null));
            return await scope.CompleteOperationKeyAsync("alice", "test", "operation-key", "{}");
        }, default)).Outcome);
        Assert.Equal(SharingScope.Host, (await vms.GetAsync("child", default))!.Sharing);
        Assert.Equal(lease, (await vms.GetAsync("child", default))!.Lease);
        Assert.Equal(7, (await new SqliteUserStore(db).GetAsync("alice", default))!.Allowance!.MaxRetainedChildren);
        Assert.False((await vms.GetOverrideAsync("parent", default))!.AllowChildCreation);
        Assert.Single(await new SqliteAuditLog(db).QueryAsync(100, default));
        Assert.Equal(AdmissionOutcome.VersionConflict, (await admission.MutateAsync(null, async scope =>
        {
            await scope.AppendAuditAsync(new(clock.UtcNow, "alice", "rolled-back", "child", AuditOutcome.Success, null));
            return await scope.UpdateLeaseAsync("child", lease, 0);
        }, default)).Outcome);
        Assert.Single(await new SqliteAuditLog(db).QueryAsync(100, default));
        Assert.Equal(0, await new SqliteOperationKeyStore(db).SweepAsync(clock.UtcNow.AddHours(-1), default));
        Assert.Equal(1, await new SqliteOperationKeyStore(db).SweepAsync(clock.UtcNow.AddHours(1), default));
    }
    [Fact]
    public async Task ScopeReserveConfirmAndReleaseUsesOneTransaction()
    {
        IReadOnlyList<string> ids = [];
        await admission.MutateAsync(null, async scope => { ids = (await scope.ReserveAsync(Plan(Vm("vm")).Reservation!)).ReservationIds; return true; }, default);
        Assert.Equal(ReservationPhase.Pending, Assert.Single((await ledger.SnapshotAsync(false, default)).Reservations).Phase);
        await admission.MutateAsync(null, async scope => { await scope.ConfirmReservationsAsync(ids, VmState.Running); return true; }, default);
        Assert.Equal(ReservationPhase.Held, Assert.Single((await ledger.SnapshotAsync(false, default)).Reservations).Phase);
        await admission.MutateAsync(null, async scope => { await scope.ReleaseReservationsAsync(ids, VmState.Off, "observed-off"); return true; }, default);
        Assert.Empty((await ledger.SnapshotAsync(false, default)).Reservations);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChildAdmissionChecksParent(bool closed)
    {
        if (closed) await vms.AddAsync(Vm("parent") with { ChildCreationClosed = true }, allowance, default);
        Assert.Equal(closed ? AdmissionOutcome.ParentClosed : AdmissionOutcome.ParentMissing,
            (await admission.AdmitAsync(Plan(Child()), default)).Outcome);
        Assert.Null(await vms.GetAsync("child", default));
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}
