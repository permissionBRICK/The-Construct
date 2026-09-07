using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Contracts;
using Constructd.Api.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Sqlite;
using Constructd.Tests.Support;
using Microsoft.Data.Sqlite;
namespace Constructd.Tests.Jobs;

public sealed class ChildVmJobTests
{
    private static async Task<HttpClient> Setup(TestApp app)
    {
        var client = await app.CreateUserClientAsync("alice");
        await app.Vms.AddAsync(new("parent", "alice", 1, 1, 1, app.Clock.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, []), 5, default);
        await app.Service<IUserAllowanceStore>().SetAllowanceAsync("alice", UserAllowance.Unset with { AllowChildCreation = true, MaxRetainedChildren = 5, AllowNeverLifetime = true }, default);
        app.Service<InMemoryCapacityLedger>().Mode = CapacityMode.Observe;
        await app.Service<IMediaStore>().AddAsync(new("install", "alice", "install.iso", MediaRole.Install, MediaSource.Upload, null, @"C:\media\install.iso", MediaState.Ready, 40960, 40960, null, null, null, null, null, app.Clock.UtcNow, app.Clock.UtcNow, null), default);
        return client;
    }
    private static object Request(bool start = true, string lifetime = "10m", string? key = "create-key-1") => new { name = "child", cpus = 1, ramMb = 512, diskGb = 1, lifetime, media = new { installMediaId = "install" }, preset = "windows", start, operationKey = key };
    private static async Task<Job> Finish(TestApp app, HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(); var id = accepted.GetProperty("jobId").GetString()!;
        var jobs = app.Service<IJobEngine>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var unused in jobs.SubscribeAsync(id, timeout.Token)) { }
        return (await jobs.GetAsync(id, default))!;
    }
    [Fact]
    public async Task CreateJobReentryCompletesStartWithoutRecreatingHardware()
    {
        await using var app = new TestApp(); using var client = await Setup(app);
        var job = await Finish(app, await client.PostAsJsonAsync("/api/v1/vms/parent/children", Request()));
        var vm = (await app.Vms.GetAsync("child", default))!;
        var before = app.Driver.Calls.ToArray();
        var holds = (await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations;
        var placement = await app.Service<IChildVmStorage>().ResolveStorageAsync(vm.Name, default);
        await app.Service<ChildCreateJob>().RunAsync(job, vm, placement, true, holds.Select(r => r.Id).ToArray(), new Progress<string>(), default);
        Assert.Equal(before.Count(c => c.StartsWith("start:", StringComparison.Ordinal)), app.Driver.Calls.Count(c => c.StartsWith("start:", StringComparison.Ordinal)));
        Assert.Equal(vm.Lease, (await app.Vms.GetAsync(vm.Name, default))!.Lease);
    }
    [Fact]
    public async Task UnsupportedAdmissionReturnsExplicitProblemWithoutAllocation()
    {
        await using var app = new TestApp(configureServices: services => services.AddSingleton<IAdmissionStore, UnsupportedFeaturePlatform>());
        using var client = await Setup(app);
        var response = await client.PostAsJsonAsync("/api/v1/vms/parent/children", Request());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("unsupported-capability", await response.Content.ReadAsStringAsync());
        Assert.Null(await app.Vms.GetAsync("child", default));
        Assert.Empty((await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnforcedCapacityIsAtomicAcrossConcurrentCreates(bool oneFits)
    {
        await using var app = new TestApp(); using var client = await Setup(app);
        var ledger = app.Service<InMemoryCapacityLedger>(); ledger.Mode = CapacityMode.Enforce;
        ledger.Inventory = new(1, app.Clock.UtcNow, true, 1L << 30, 0, 0, 0, 1L << 30, oneFits ? 512L << 20 : 0, 1, 1, 0, 1,
            [new(@"C:\", 10L << 30, 10L << 30, 0, 0, 10L << 30)], [], []);
        var responses = await Task.WhenAll(Enumerable.Range(1, 2).Select(i => client.PostAsJsonAsync("/api/v1/vms/parent/children",
            new { name = "child" + i, cpus = 1, ramMb = 512, diskGb = 1, lifetime = "10m", media = new { installMediaId = "install" }, preset = "windows", operationKey = "capacity-key-" + i })));
        Assert.Equal(oneFits ? 1 : 0, responses.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        foreach (var response in responses)
            if (response.StatusCode == HttpStatusCode.Accepted) Assert.Equal(JobState.Succeeded, (await Finish(app, response)).State);
            else { Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Contains("capacity-exhausted", await response.Content.ReadAsStringAsync()); }
        Assert.Equal(oneFits ? 4 : 0, (await ledger.SnapshotAsync(false, default)).Reservations.Count);
        Assert.Equal(oneFits ? 1 : 0, app.Driver.Calls.Count(c => c.StartsWith("start:child", StringComparison.Ordinal)));
    }
    [Theory]
    [InlineData(VmState.Running, false, false, false, false, null)]
    [InlineData(VmState.Off, false, false, false, false, null)]
    [InlineData(VmState.Saved, false, false, true, false, null)]
    [InlineData(VmState.Off, true, false, false, false, "power-state-changed")]
    [InlineData(VmState.Off, false, true, false, false, "intent-expired")]
    [InlineData(VmState.Unknown, false, false, false, false, "vm-state-unknown")]
    [InlineData(VmState.Off, false, false, true, true, "capacity-unavailable")]
    public async Task StartIntentReconcilesOriginalClockAndGeneration(VmState state, bool moved, bool expired, bool swept, bool refuse, string? error)
    {
        await using var app = new TestApp(); using var client = await Setup(app);
        var job = await Finish(app, await client.PostAsJsonAsync("/api/v1/vms/parent/children", Request(false)));
        var vm = (await app.Vms.GetAsync("child", default))!;
        var ledger = app.Service<ICapacityLedger>();
        var power = await ledger.TryReserveAsync(new(vm.Owner, vm.Name, job.Id,
            [new(ReservationResource.Ram, vm.RamBytes, null, null), new(ReservationResource.Cpu, vm.Cpu, null, null)], TimeSpan.FromHours(2)), default);
        var holds = (await ledger.SnapshotAsync(false, default)).Reservations;
        var acceptedAt = app.Clock.UtcNow.AddSeconds(expired ? -601 : -60);
        var intent = new ChildStartIntent.Intent(600, vm.Lease!.Version, acceptedAt, holds.Select(r => new ReservationLine(r.Resource, r.Amount, r.Artifact, r.Volume)).ToArray());
        await app.Service<IOperationKeyStore>().TryInsertAsync(new(vm.Owner, "child-start", job.Id + ":start", job.Id, vm.Name, job.Id,
            OperationKeyState.InFlight, JsonSerializer.Serialize(intent), vm.PowerGeneration, null, acceptedAt), default);
        if (moved) await app.Service<IAdmissionStore>().MutateAsync(null, scope => scope.BumpPowerGenerationAsync(vm.Name, vm.PowerGeneration), default);
        if (swept) await ledger.ReleaseAsync(power.ReservationIds, VmState.Off, "orphan evidence", default);
        if (refuse) app.Service<InMemoryCapacityLedger>().Mode = CapacityMode.Enforce;
        app.Driver.SetState(vm.Name, state);
        var starter = app.Service<ChildStartIntent>();
        if (error is not null)
        {
            var exception = await Assert.ThrowsAsync<ChildValidationException>(() => starter.RunAsync(job, vm, holds.Select(r => r.Id).ToArray(), default));
            Assert.Equal(error, exception.Code);
            Assert.DoesNotContain("start:child", app.Driver.Calls);
        }
        else
        {
            var result = await starter.RunAsync(job, vm, holds.Select(r => r.Id).ToArray(), default);
            Assert.Equal(acceptedAt, result.Lease!.ActivatedAt); Assert.Equal(acceptedAt.AddSeconds(600), result.Lease.ExpiresAt);
            Assert.Equal(vm.PowerGeneration + 1, result.PowerGeneration);
            var replay = await starter.RunAsync(job, vm, holds.Select(r => r.Id).ToArray(), default);
            Assert.Equal(result.Lease, replay.Lease);
            Assert.Equal(state == VmState.Running ? 0 : 1, app.Driver.Calls.Count(c => c == "start:child"));
            Assert.Equal(4, (await ledger.SnapshotAsync(false, default)).Reservations.Count);
        }
        var key = await app.Service<IOperationKeyStore>().GetAsync(vm.Owner, "child-start", job.Id + ":start", default);
        Assert.Equal(error is "power-state-changed" or "vm-state-unknown" ? OperationKeyState.InFlight : OperationKeyState.Completed, key!.State);
    }
    [Theory]
    [InlineData(true, "10m", LeaseState.Active)]
    [InlineData(false, "10m", LeaseState.Inactive)]
    [InlineData(true, "never", LeaseState.Unlimited)]
    public async Task CreateReportsBootFactsAndNeverProvisions(bool start, string lifetime, LeaseState leaseState)
    {
        await using var app = new TestApp(); using var client = await Setup(app);
        var job = await Finish(app, await client.PostAsJsonAsync("/api/v1/vms/parent/children", Request(start, lifetime)));
        Assert.Equal(JobState.Succeeded, job.State); Assert.Equal("done", job.Phase);
        var vm = (await app.Vms.GetAsync("child", default))!;
        Assert.Equal(start ? VmState.Running : VmState.Off, vm.State); Assert.Equal(leaseState, vm.Lease!.State);
        Assert.NotNull(vm.Incarnation); Assert.Null(vm.VmTokenHash); Assert.Null(vm.Guest); Assert.Null(vm.SshForwardPort);
        Assert.Equal("parent-child", Assert.Single(await app.Service<INetworkPolicyReconciler>().ListRulesAsync("child", default)).Kind);
        Assert.Null(await app.Service<IJobEngine>().TakeOneTimeSecretAsync(job.Id, default));
        Assert.DoesNotContain(app.Driver.Calls, x => x.Contains("reachable", StringComparison.OrdinalIgnoreCase));
        var holds = (await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations;
        Assert.Equal(start ? 4 : 2, holds.Count); Assert.All(holds, x => Assert.Equal(ReservationPhase.Held, x.Phase));
        Assert.Equal(0, app.Service<IMaintenanceGate>().LiveHandles);
    }
    [Fact]
    public async Task ReplayReturnsOriginalAndConflictingRequestIsRejected()
    {
        await using var app = new TestApp(); using var client = await Setup(app);
        var first = await Finish(app, await client.PostAsJsonAsync("/api/v1/vms/parent/children", Request()));
        var replay = await client.PostAsJsonAsync("/api/v1/vms/parent/children", Request());
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(first.Id, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/vms/parent/children", Request(false))).StatusCode);
        Assert.Single(app.Service<FakeChildVmDriver>().Calls, x => x.StartsWith("create:"));
    }
    [Fact]
    public async Task DeleteRemovesReferencesAndAllChildHolds()
    {
        await using var app = new TestApp(); using var client = await Setup(app);
        Assert.Equal(JobState.Succeeded, (await Finish(app, await client.PostAsJsonAsync("/api/v1/vms/parent/children", Request()))).State);
        Assert.Single(await app.Service<INetworkPolicyReconciler>().ListRulesAsync("child", default));
        var deleted = await Finish(app, await client.DeleteAsync("/api/v1/vms/child"));
        Assert.Equal(JobState.Succeeded, deleted.State);
        Assert.Empty(await app.Service<INetworkPolicyReconciler>().ListRulesAsync("child", default));
        Assert.Null(await app.Vms.GetAsync("child", default));
        Assert.Empty(await app.Service<IMediaStore>().ListReferencesForVmAsync("child", default));
        Assert.Empty((await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations);
        Assert.NotNull(await app.Service<IMediaStore>().GetAsync("install", default));
    }
    [Fact]
    public async Task FailedStartRollsBackHardwareAndRetainsSharedMedia()
    {
        await using var app = new TestApp(); using var client = await Setup(app);
        app.Driver.PowerFailure = new InvalidOperationException("start failure");
        var job = await Finish(app, await client.PostAsJsonAsync("/api/v1/vms/parent/children", Request()));
        Assert.Equal(JobState.Failed, job.State);
        Assert.Null(await app.Vms.GetAsync("child", default));
        Assert.Empty((await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations);
        Assert.NotNull(await app.Service<IMediaStore>().GetAsync("install", default));
    }
    [Theory]
    [InlineData(1, 512, 1, "4m")]
    [InlineData(0, 512, 1, "10m")]
    [InlineData(1, 513, 1, "10m")]
    [InlineData(1, 512, 0, "10m")]
    public async Task InvalidShapeAllocatesNothing(int cpus, int ramMb, int diskGb, string lifetime)
    {
        await using var app = new TestApp(); using var client = await Setup(app);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/vms/parent/children", new { cpus, ramMb, diskGb, lifetime, media = new { installMediaId = "install" } })).StatusCode);
        Assert.Empty(app.Service<FakeChildVmDriver>().Calls);
    }
    [Fact]
    public async Task UnsupportedHardwareAndMaintenanceAllocateNothing()
    {
        await using var app = new TestApp(); using var client = await Setup(app);
        var driver = app.Service<FakeChildVmDriver>(); driver.Capabilities = driver.Capabilities with { Tpm = CapabilityLevel.Unsupported };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/vms/parent/children", Request())).StatusCode);
        app.Service<IMaintenanceGate>().Enter(MaintenanceState.Maintenance, "update");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/v1/vms/parent/children", Request())).StatusCode);
        Assert.Null(await app.Vms.GetAsync("child", default)); Assert.Empty(driver.Calls);
    }
    [Fact]
    public async Task FailedRollbackRetainsAccountingAndDeleteCanRetry()
    {
        await using var app = new TestApp(); using var client = await Setup(app);
        var driver = app.Service<FakeChildVmDriver>();
        driver.FailureAfterCreate = new IOException("dependency secret");
        driver.RemoveFailure = new IOException("dependency secret");
        var job = await Finish(app, await client.PostAsJsonAsync("/api/v1/vms/parent/children", Request()));
        Assert.Equal(JobState.Failed, job.State); Assert.DoesNotContain("dependency secret", job.Error);
        Assert.NotNull(await app.Vms.GetAsync("child", default));
        Assert.Equal(4, (await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations.Count);
        Assert.Single(await app.Service<IMediaStore>().ListReferencesForVmAsync("child", default));
        driver.RemoveFailure = null;
        var deleted = await Finish(app, await client.DeleteAsync("/api/v1/vms/child"));
        Assert.Equal(JobState.Succeeded, deleted.State);
        Assert.Empty((await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations);
    }
    [Fact]
    public async Task ForeignIncarnationSurvivesDeleteAndItsCapacityIsRetained()
    {
        await using var app = new TestApp(); using var client = await Setup(app);
        await Finish(app, await client.PostAsJsonAsync("/api/v1/vms/parent/children", Request()));
        var driver = app.Service<IChildVmDriver>();
        var original = (await app.Vms.GetAsync("child", default))!;
        await driver.RemoveAsync("child", null, default);
        await driver.CreateAsync(new("child", original.Hardware!, null, @"C:\media\install.iso", null, "Default Switch"), null, default);
        var deleted = await Finish(app, await client.DeleteAsync("/api/v1/vms/child"));
        Assert.Equal(JobState.Failed, deleted.State);
        Assert.NotNull(await app.Service<IChildVmDriver>().GetVmIdAsync("child", default));
        Assert.Equal(4, (await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations.Count);
    }
    [Fact]
    public async Task PersistedRunnerHoldsDrainAcrossAdmissionAndStreamsPhases()
    {
        var clock = new MutableClock(); var store = new InMemoryJobStore();
        using var engine = new InProcessJobEngine(clock, store);
        var maintenance = new InMemoryMaintenanceGate();
        var job = new Job("persisted", "child-create", "child", "alice", JobState.Queued, [], null, null, clock.UtcNow, null);
        using var handle = maintenance.TryEnter(job.Kind, job.Id, job.VmName)!;
        await store.UpsertAsync(job, default);
        var drain = maintenance.DrainAsync(TimeSpan.FromSeconds(5), default);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var begin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await engine.StartPersistedAsync(job, handle, async (_, ct) => { await begin.Task; await engine.SetPhaseAsync(job.Id, "hardware", ct); await finish.Task; return new JobOutcome(null); }, default);
        Assert.False(drain.IsCompleted);
        await using var events = engine.SubscribeAsync(job.Id, default).GetAsyncEnumerator();
        var next = events.MoveNextAsync();
        begin.SetResult(); Assert.True(await next);
        Assert.Equal(JobEventKind.Phase, events.Current.Kind); Assert.Equal("hardware", events.Current.Progress!.Text);
        finish.SetResult(); Assert.True(await events.MoveNextAsync());
        Assert.Equal(JobState.Succeeded, events.Current.Job!.State);
        Assert.True((await drain).Drained);
        Assert.Equal("hardware", (await store.GetAsync(job.Id, default))!.Phase);
    }

    [Fact]
    public async Task LongRunningIntentRetentionStartsAtCompletion()
    {
        var path = Path.Combine(Path.GetTempPath(), "child-key-retention-" + Guid.NewGuid().ToString("n") + ".db");
        try
        {
            var db = new SqliteDatabase(path); db.EnsureCreated();
            foreach (var store in new IOperationKeyStore[] { new InMemoryOperationKeyStore(), new SqliteOperationKeyStore(db) })
            {
                var now = DateTimeOffset.UtcNow;
                await store.TryInsertAsync(new("alice", "child-start", "retention-key", "fp", "child", null, OperationKeyState.InFlight, "{}", 0, null, now.AddDays(-3)), default);
                Assert.True(await store.CompleteAsync("alice", "child-start", "retention-key", "null", default));
                Assert.Equal(0, await store.SweepAsync(now.AddDays(-1), default));
                Assert.Equal(1, await store.SweepAsync(now.AddDays(2), default));
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task KeySweepRetainsLiveJobsInBothStores()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n")); Directory.CreateDirectory(directory);
        try
        {
            var db = new SqliteDatabase(Path.Combine(directory, "test.db")); db.EnsureCreated();
            var memoryJobs = new InMemoryJobStore();
            foreach (var pair in new (IOperationKeyStore Keys, IJobStore Jobs)[] { (new InMemoryOperationKeyStore(memoryJobs), memoryJobs), (new SqliteOperationKeyStore(db), new SqliteJobStore(db)) })
            {
                var at = DateTimeOffset.UtcNow.AddDays(-8);
                var job = new Job("job", "child-create", "child", "alice", JobState.Running, [], null, null, at, null);
                await pair.Jobs.UpsertAsync(job, default);
                await pair.Keys.TryInsertAsync(new("alice", "child-create", "key", "fingerprint", "child", job.Id, OperationKeyState.Completed, null, null, "{}", at), default);
                Assert.Equal(0, await pair.Keys.SweepAsync(DateTimeOffset.UtcNow, default));
                await pair.Jobs.UpsertAsync(job with { State = JobState.Failed, Finished = DateTimeOffset.UtcNow }, default);
                Assert.Equal(1, await pair.Keys.SweepAsync(DateTimeOffset.UtcNow, default));
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public void FingerprintsCanonicalizeObjectsAndNormalizeStringsButKeepNumericSpelling()
    {
        using var a = JsonDocument.Parse("{\"b\":\"e\\u0301\",\"a\":1,\"operationKey\":\"one\"}");
        using var b = JsonDocument.Parse("{\"a\":1,\"b\":\"é\"}");
        using var c = JsonDocument.Parse("{\"a\":1.0,\"b\":\"é\"}");
        Assert.Equal(OperationFingerprint.Compute("route", a.RootElement), OperationFingerprint.Compute("route", b.RootElement));
        Assert.NotEqual(OperationFingerprint.Compute("route", b.RootElement), OperationFingerprint.Compute("route", c.RootElement));
        Assert.Equal(OperationFingerprint.ChildName("parent", "alice", "key"), OperationFingerprint.ChildName("parent", "alice", "key"));
    }
    [Fact]
    public async Task SqliteKeysAreDurableAndConflictsDoNotOverwrite()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n")); Directory.CreateDirectory(directory);
        try
        {
            var db = new SqliteDatabase(Path.Combine(directory, "test.db")); db.EnsureCreated();
            var store = new SqliteOperationKeyStore(db);
            var record = new OperationKeyRecord("alice", "child-create", "key", "fingerprint", "child", null, OperationKeyState.InFlight, null, null, null, DateTimeOffset.UtcNow);
            Assert.Equal(OperationKeyOutcome.Inserted, (await store.TryInsertAsync(record, default)).Outcome);
            Assert.Equal(OperationKeyOutcome.Replay, (await store.TryInsertAsync(record, default)).Outcome);
            Assert.Equal(OperationKeyOutcome.Conflict, (await store.TryInsertAsync(record with { Fingerprint = "other" }, default)).Outcome);
            Assert.True(await store.CompleteAsync("alice", "child-create", "key", "{}", default));
            Assert.Equal(OperationKeyState.Completed, (await new SqliteOperationKeyStore(db).GetAsync("ALICE", "child-create", "key", default))!.State);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
}
