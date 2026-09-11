using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Sqlite;
using Constructd.Tests.Support;
using Microsoft.Data.Sqlite;
namespace Constructd.Tests.Source;

public sealed class SourceAdmissionTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task PinJobAndCompleteReplayAreAtomic(bool sqlite, bool queued, bool keyed)
    {
        var root = Path.Combine(Path.GetTempPath(), "source-admission-" + Guid.NewGuid().ToString("n"));
        try
        {
            await using var app = sqlite ? TestApp.WithSqlite(Path.Combine(root, "test.db")) : new TestApp();
            using var owner = await app.CreateUserClientAsync("alice");
            var admission = app.Service<IAdmissionStore>(); var commit = new string('a', 40);
            var vm = new Vm("vm", "alice", 1, 1, 8, app.Clock.UtcNow, VmState.Off, null, null, IdlePolicy.Disabled, []);
            await app.Vms.AddAsync(vm, 5, default);
            var job = queued ? new Job("source-job", "source-fetch", "vm", "alice", JobState.Queued, [], null, null, app.Clock.UtcNow, null) : null;
            var body = queued ? "{\"jobId\":\"source-job\",\"state\":\"downloading\",\"commit\":\"" + commit + "\"}" : "{\"state\":\"ready\",\"commit\":\"" + commit + "\",\"sizeBytes\":123,\"sha256\":\"" + new string('b',64) + "\"}";
            var key = keyed ? new OperationKeyRecord("alice", "source-ensure", "source-key", "fingerprint", "vm", job?.Id,
                OperationKeyState.Completed, null, null, body, app.Clock.UtcNow) : null;
            var plan = new AdmissionPlan(key, null, null, [], [], [], null, null, job, null, null, false, VmSourceCommit: ("vm", commit));
            Assert.Equal(AdmissionOutcome.Accepted, (await admission.AdmitAsync(plan, default)).Outcome);
            Assert.Equal(commit, (await app.Vms.GetAsync("vm", default))!.SourceCommit);
            // A stale lifecycle update must never overwrite the new metadata.
            await app.Vms.UpdateAsync(vm, default);
            Assert.Equal(commit, (await app.Vms.GetAsync("vm", default))!.SourceCommit);
            if (keyed)
            {
                var replay = await admission.AdmitAsync(plan, default);
                Assert.Equal(AdmissionOutcome.Replay, replay.Outcome); Assert.Equal(body, replay.ExistingKey!.ResponseJson);
                Assert.Equal(AdmissionOutcome.KeyConflict, (await admission.AdmitAsync(plan with { OperationKey = key! with { Fingerprint = "other" } }, default)).Outcome);
            }
            if (queued)
            {
                await admission.MarkStartFailedAsync(job!.Id, "job-start-failed", default);
                Assert.Equal(JobState.Failed, (await app.Service<IJobStore>().GetAsync(job.Id, default))!.State);
                Assert.Equal("job-start-failed", (await app.Service<IJobStore>().GetAsync(job.Id, default))!.Error);
                if (keyed) Assert.Equal(body, (await admission.AdmitAsync(plan, default)).ExistingKey!.ResponseJson);
            }
            var failedPlan = plan with { OperationKey = key is null ? null : key with { Key = "new" }, JobToInsert = job is null ? null : job with { Id = "new" }, VmSourceCommit = ("missing", commit) };
            Assert.Equal(AdmissionOutcome.VersionConflict, (await admission.AdmitAsync(failedPlan, default)).Outcome);
            Assert.Null(await app.Service<IOperationKeyStore>().GetAsync("alice", "source-ensure", "new", default));
            Assert.Null(await app.Service<IJobStore>().GetAsync("new", default));
            await app.Vms.UpdateAsync(vm with { Deleting = true }, default);
            Assert.Equal(AdmissionOutcome.VersionConflict, (await admission.AdmitAsync(failedPlan with { VmSourceCommit = ("vm", commit) }, default)).Outcome);
            Assert.Equal(commit, (await app.Vms.GetAsync("vm", default))!.SourceCommit);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnforceAdmissionNeverReadsExpiredInventory(bool queued)
    {
        var root = Path.Combine(Path.GetTempPath(), "source-inventory-" + Guid.NewGuid().ToString("n"));
        try
        {
            var clock = new MutableClock(DateTimeOffset.UtcNow); var db = new SqliteDatabase(Path.Combine(root,"test.db")); db.EnsureCreated();
            var inventory = new BlockingInventory();
            await new SqliteHostConfigStore(db, clock).SetAsync("capacity", HostAdminDefaults.Capacity with { Mode = CapacityMode.Enforce }, "admin", default);
            var ledger = new SqliteCapacityLedger(db, clock, inventory, new ConstructdOptions());
            var vms = new SqliteVmRepository(db, clock);
            await vms.AddAsync(new("vm", "alice", 1, 1, 8, clock.UtcNow, VmState.Off, null, null, IdlePolicy.Disabled, []), 5, default);
            var plan = new AdmissionPlan(null, null, null, [], [], [], null, null,
                queued ? new("job", "source-fetch", "vm", "alice", JobState.Queued, [], null, null, clock.UtcNow, null) : null,
                null, null, false, VmSourceCommit: ("vm", new string('a',40)));
            using var fixture = new SourceFixture(); fixture.Add(SourceFixture.Other);
            var catalog = new Constructd.Api.Source.SourceCatalog();
            var cache = new Constructd.Api.Source.SourceCache(new SqliteSourceStore(db), fixture.Files, fixture.Releases, fixture.Gates,
                catalog, fixture.Options, new SqliteHostConfigStore(db, clock), clock, fixture.Audit);
            await cache.EnsureAsync(SourceFixture.Other, null, default);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var accepted = new SqliteAdmissionStore(ledger, clock).AdmitAsync(plan, cancellation.Token);
            var open = await cache.OpenAsync(SourceFixture.Other, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(2));
            await open.Stream.DisposeAsync();
            Assert.Equal(AdmissionOutcome.Accepted, (await accepted.WaitAsync(TimeSpan.FromSeconds(2))).Outcome);
            Assert.Equal(0, inventory.Reads);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root,true); }
    }
    private sealed class BlockingInventory : IHypervisorInventory
    {
        public int Reads { get; private set; }
        public async Task<InventorySnapshot> ReadAsync(CancellationToken ct)
        { Reads++; await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); }
    }

}
