using System.Net;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Tests.Support;
namespace Constructd.Tests.Foundation;

public sealed class SeamTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.2.3.4")]
    [InlineData("10.0.0.1")]
    [InlineData("172.31.0.1")]
    [InlineData("192.168.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("192.0.0.2")]
    [InlineData("198.19.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("ff02::1")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("::192.168.1.1")]
    [InlineData("::")]
    public void UrlRulesRejectEveryNonPublicClassEvenWithOnePublicAnswer(string refused)
    {
        var result = new UrlAdmissionRules().Check(new("https://example.org/os.iso"), [IPAddress.Parse("8.8.8.8"), IPAddress.Parse(refused)], false, false);
        Assert.False(result.Allowed); Assert.Equal("address", result.Reason);
    }
    [Fact]
    public void UrlRulesPermitPublicAddressesAndRequireChecksumForHttp()
    {
        var rules = new UrlAdmissionRules(); IPAddress[] addresses = [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2606:4700::1111")];
        Assert.True(rules.Check(new("https://example.org/a"), addresses, false, false).Allowed);
        Assert.True(rules.Check(new("http://example.org/a"), addresses, true, true).Allowed);
        Assert.False(rules.Check(new("http://example.org/a"), addresses, true, false).Allowed);
        Assert.False(rules.Check(new("https://user@example.org/a"), addresses, true, true).Allowed);
        Assert.False(rules.Check(new("https://127.0.0.1/a"), addresses, true, true).Allowed);
        Assert.False(rules.Check(new("https://example.org/a"), [], true, true).Allowed);
    }
    [Fact]
    public async Task AdmissionRollsBackAllParticipantsAndReplaysWithoutRerunningMutation()
    {
        using var app = new TestApp();
        await app.AddUserAsync("alice");
        var admission = app.Service<IAdmissionStore>(); var ledger = app.Service<InMemoryCapacityLedger>();
        ledger.Mode = CapacityMode.Enforce;
        ledger.Inventory = ledger.Inventory with { Complete = true, RamAvailableBytes = 1024, RamPhysicalFreeBytes = 1024, RamTotalBytes = 1024 };
        var vm = new Vm("parent", "alice", 1, 1, 10, app.Clock.UtcNow, VmState.Off, null, null, IdlePolicy.Disabled, []);
        var key = new OperationKeyRecord("alice", "create", "key", "fp", "parent", "job", OperationKeyState.InFlight, null, null, null, app.Clock.UtcNow);
        var job = new Job("job", "create", "parent", "alice", JobState.Queued, [], null, null, app.Clock.UtcNow, null);
        var plan = new AdmissionPlan(key, vm, new(3, true, 3, null, null, null, null, true, true, true), [], [], [],
            new("alice", "parent", "job", [new(ReservationResource.Ram, 2048, null, null)], TimeSpan.FromMinutes(5)), null, job, null, null, false);
        Assert.Equal(AdmissionOutcome.CapacityRefused, (await admission.AdmitAsync(plan, Ct)).Outcome);
        Assert.Null(await app.Vms.GetAsync("parent", Ct)); Assert.Null(await app.Service<IJobStore>().GetAsync("job", Ct));
        Assert.Null(await app.Service<IOperationKeyStore>().GetAsync("alice", "create", "key", Ct)); Assert.Empty((await ledger.SnapshotAsync(false, Ct)).Reservations);
        plan = plan with { Reservation = plan.Reservation! with { Lines = [new(ReservationResource.Ram, 1024, null, null)] } };
        Assert.Equal(AdmissionOutcome.Accepted, (await admission.AdmitAsync(plan, Ct)).Outcome);
        Assert.Equal(AdmissionOutcome.Replay, (await admission.AdmitAsync(plan, Ct)).Outcome);
        Assert.Single((await ledger.SnapshotAsync(false, Ct)).Reservations);
        var changeKey = key with { Kind = "allowance", Key = "change", State = OperationKeyState.Completed, ResponseJson = "{}" };
        Assert.Equal(AdmissionOutcome.VersionConflict, (await admission.MutateAsync(changeKey, async scope =>
        {
            await scope.SetAllowanceAsync("alice", UserAllowance.Unset with { MaxRetainedChildren = 8 });
            await scope.AppendAuditAsync(new(app.Clock.UtcNow, "alice", "test", "host", AuditOutcome.Success, null));
            return await scope.BumpPowerGenerationAsync("parent", 99);
        }, Ct)).Outcome);
        Assert.Null((await app.Users.GetAsync("alice", Ct))!.Allowance!.MaxRetainedChildren);
        Assert.Empty(await app.Service<IAuditLog>().QueryAsync(100, Ct));
        Assert.Null(await app.Service<IOperationKeyStore>().GetAsync("alice", "allowance", "change", Ct));
        var count = 0;
        Task<bool> Mutate(IAdmissionScope scope) { count++; return scope.SetAllowanceAsync("alice", UserAllowance.Unset with { MaxRetainedChildren = 8 }); }
        Assert.Equal(AdmissionOutcome.Accepted, (await admission.MutateAsync(changeKey, Mutate, Ct)).Outcome);
        Assert.Equal(AdmissionOutcome.Replay, (await admission.MutateAsync(changeKey, Mutate, Ct)).Outcome);
        Assert.Equal(1, count); Assert.Equal(8, (await app.Users.GetAsync("alice", Ct))!.Allowance!.MaxRetainedChildren);
        await admission.MarkStartFailedAsync("job", "not retained", Ct);
        Assert.Equal(JobState.Failed, (await app.Service<IJobStore>().GetAsync("job", Ct))!.State);
        Assert.Single((await ledger.SnapshotAsync(false, Ct)).Reservations); // no external absence evidence
    }
    [Fact]
    public async Task AdmissionExceptionsRollBackAndEscapedScopeCannotWrite()
    {
        using var app = new TestApp(); await app.AddUserAsync("alice");
        IAdmissionScope? escaped = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Service<IAdmissionStore>().MutateAsync(null, async scope =>
        {
            escaped = scope; await scope.SetAllowanceAsync("alice", UserAllowance.Unset with { MaxRetainedChildren = 9 });
            throw new InvalidOperationException("fixture");
        }, Ct));
        Assert.Null((await app.Users.GetAsync("alice", Ct))!.Allowance!.MaxRetainedChildren);
        await Assert.ThrowsAsync<InvalidOperationException>(() => escaped!.SetAllowanceAsync("alice", UserAllowance.Unset));
    }
    [Fact]
    public async Task ConcurrentReservationsCannotSpendTheLastRamTwice()
    {
        var ledger = new InMemoryCapacityLedger(new MutableClock());
        ledger.Inventory = ledger.Inventory with { Complete = true, RamAvailableBytes = 1024 };
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Task.Run(() => ledger.TryReserveAsync(new("alice", "vm", i.ToString(), [new(ReservationResource.Ram, 1024, null, null)], TimeSpan.FromMinutes(5)), Ct))));
        Assert.Single(results, r => r.Allowed); Assert.Single((await ledger.SnapshotAsync(false, Ct)).Reservations);
    }
    [Fact]
    public async Task MaintenanceDrainClosesAdmissionBeforeLastHandleLeaves()
    {
        var gate = new InMemoryMaintenanceGate(); var handle = gate.TryEnter("create", "1", "vm")!;
        var drain = gate.DrainAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.Null(gate.TryEnter("create", "2", "vm")); Assert.False(drain.IsCompleted);
        handle.Dispose(); Assert.True((await drain).Drained); Assert.Null(gate.TryEnter("create", "3", "vm"));
        gate.Reopen(); using var reopened = gate.TryEnter("create", "4", "vm"); Assert.NotNull(reopened);
    }
    [Fact]
    public async Task FakeMediaUsesScopedFilesAndRefusesBadChecksumAndOversizedChunk()
    {
        using var transfer = new FakeMediaTransfer(); var uri = new Uri("https://example.org/a.iso");
        var bytes = new byte[32775]; bytes[32768] = 1; bytes[32774] = 1; "CD001"u8.CopyTo(bytes.AsSpan(32769)); transfer.Sources[uri] = bytes;
        var item = new MediaItem("id", "alice", "a.iso", MediaRole.Install, MediaSource.Url, null, "id.iso", MediaState.Pending, null, 40000, null, null, null, null, null, DateTimeOffset.UtcNow, null, null);
        var result = await transfer.AcquireAsync(item, uri, 40000, TimeSpan.FromMinutes(1), null, Ct);
        Assert.Equal(result.Sha256, await transfer.HashAsync(item.Path, null, Ct)); Assert.True(await transfer.LooksLikeIsoAsync(item.Path, Ct));
        await Assert.ThrowsAsync<MediaException>(() => transfer.AcquireAsync(item with { ExpectedSha256 = "bad" }, uri, 40000, TimeSpan.FromMinutes(1), null, Ct));
        var upload = new MediaUpload("up", "id", "alice", 4, 4, [], UploadState.Open, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
        await Assert.ThrowsAsync<IOException>(() => transfer.WriteChunkAsync(upload, 0, new MemoryStream(new byte[5]), 4, Ct));
        transfer.FilesHeldOpen = true; Assert.False(await transfer.TryDeleteAsync(item.Path, Ct));
        transfer.FilesHeldOpen = false; Assert.True(await transfer.TryDeleteAsync(item.Path, Ct));
        await Assert.ThrowsAsync<IOException>(() => transfer.TryDeleteAsync("../outside", Ct));
    }
    [Fact]
    public async Task FakeHostLockIsExclusiveAndDisposable()
    {
        var gate = new FakeHostLock(); await using var first = await gate.TryAcquireAsync("admin", TimeSpan.Zero, Ct);
        Assert.NotNull(first); Assert.Null(await gate.TryAcquireAsync("admin", TimeSpan.Zero, Ct));
        await first!.DisposeAsync(); await using var next = await gate.TryAcquireAsync("admin", TimeSpan.Zero, Ct); Assert.NotNull(next);
        gate.HeldByAnotherProcess = true; Assert.True(gate.IsHeldByAnotherProcess("updater")); Assert.Null(await gate.TryAcquireAsync("updater", TimeSpan.Zero, Ct));
    }
}
