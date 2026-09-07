using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Microsoft.Data.Sqlite;
namespace Constructd.Tests.Delegation;

public sealed class AdmissionParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameInvalidPlansHaveSameOutcomes(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "admission-parity-" + Guid.NewGuid().ToString("n"));
        try
        {
            await using var app = sqlite ? TestApp.WithSqlite(Path.Combine(root, "test.db")) : new TestApp();
            using var owner = await app.CreateUserClientAsync("alice");
            var admission = app.Service<IAdmissionStore>(); var clock = app.Clock;
            var vm = new Vm("vm", "alice", 1, 1, 8, clock.UtcNow, VmState.Off, null, null, IdlePolicy.Disabled, []);
            await app.Vms.AddAsync(vm, 5, default);
            var job = new Job("job", "test", "vm", "alice", JobState.Queued, [], null, null, clock.UtcNow, null);
            var plan = new AdmissionPlan(null, null, null, [], [], [], null, null, job, null, null, false);
            Assert.Equal(AdmissionOutcome.VersionConflict, (await admission.AdmitAsync(plan with { VmToFence = "vm" }, default)).Outcome);
            Assert.Equal(AdmissionOutcome.VersionConflict, (await admission.AdmitAsync(plan with { JobToInsert = job with { State = JobState.Running } }, default)).Outcome);
            Assert.Equal(AdmissionOutcome.Accepted, (await admission.AdmitAsync(plan, default)).Outcome);
            Assert.Equal(AdmissionOutcome.VersionConflict, (await admission.AdmitAsync(plan, default)).Outcome);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => admission.MarkStartFailedAsync("missing", "ignored", default));
            Assert.False((await app.Vms.GetAsync("vm", default))!.Deleting);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteChildCreateAndPartialRuntimeSweepRecovery(bool swept)
    {
        var root = Path.Combine(Path.GetTempPath(), "sqlite-child-recovery-" + Guid.NewGuid().ToString("n"));
        try
        {
            await using var app = TestApp.WithSqlite(Path.Combine(root, "test.db"));
            using var owner = await app.CreateUserClientAsync("alice");
            await app.Vms.AddAsync(new("parent", "alice", 1, 1, 8, app.Clock.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, []), 5, default);
            await app.Service<IMediaStore>().AddAsync(new("install", "alice", "test.iso", MediaRole.Install, MediaSource.Upload, null,
                @"C:\media\install.iso", MediaState.Ready, 40960, 40960, null, null, null, null, null, app.Clock.UtcNow, app.Clock.UtcNow, null), default);
            var response = await owner.PostAsJsonAsync("/api/v1/vms/parent/children", new { name = "child", cpus = 1, ramMb = 512, diskGb = 8,
                lifetime = "10m", media = new { installMediaId = "install" }, start = true, operationKey = "sqlite-create" });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;
            var jobs = app.Service<IJobEngine>(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await foreach (var unused in jobs.SubscribeAsync(id, timeout.Token)) { }
            var job = (await jobs.GetAsync(id, default))!; Assert.Equal(JobState.Succeeded, job.State);
            var vm = (await app.Vms.GetAsync("child", default))!;
            Assert.NotNull(vm.Incarnation); Assert.Equal(LeaseState.Active, vm.Lease!.State); Assert.Null(vm.VmTokenHash);
            Assert.Single(await app.Service<IMediaStore>().ListReferencesForVmAsync("child", default));
            var ledger = app.Service<ICapacityLedger>(); var keys = app.Service<IOperationKeyStore>();
            var holds = (await ledger.SnapshotAsync(false, default)).Reservations.Where(r => r.VmName == "child").ToArray();
            Assert.Equal(4, holds.Length);
            // Reconstruct the durable intent at a lost-completion boundary, keeping real create artifacts.
            await keys.RemoveAsync(vm.Owner, "child-start", id + ":start", default);
            var intent = new ChildStartIntent.Intent(600, vm.Lease.Version, app.Clock.UtcNow,
                holds.Select(r => new ReservationLine(r.Resource, r.Amount, r.Artifact, r.Volume)).ToArray());
            await keys.TryInsertAsync(new(vm.Owner, "child-start", id + ":start", id, vm.Name, id,
                OperationKeyState.InFlight, JsonSerializer.Serialize(intent), vm.PowerGeneration, null, app.Clock.UtcNow), default);
            if (swept) await ledger.ReleaseAsync(holds.Where(r => r.Resource != ReservationResource.Storage).Select(r => r.Id).ToArray(), VmState.Off, "orphan evidence", default);
            app.Driver.SetState("child", VmState.Off); app.Clock.Advance(TimeSpan.FromMinutes(1));
            await using var gate = await app.Service<IVmOperationGate>().AcquireAsync("child", id, default);
            var before = app.Driver.Calls.Count(c => c == "start:child");
            var recovered = await app.Service<ChildStartIntent>().RunAsync(job, vm, holds.Select(r => r.Id).ToArray(), default);
            Assert.Equal(intent.ActivationBase.AddMinutes(10), recovered.Lease!.ExpiresAt);
            Assert.Equal(before + 1, app.Driver.Calls.Count(c => c == "start:child"));
            Assert.Equal(4, (await ledger.SnapshotAsync(false, default)).Reservations.Count(r => r.VmName == "child"));
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
