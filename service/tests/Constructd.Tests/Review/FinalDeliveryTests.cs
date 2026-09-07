using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Sqlite;
using Constructd.Sqlite.Migrations;
using Constructd.Tests.Delegation;
using Constructd.Tests.Support;
using Constructd.Windows.Updates;
using Microsoft.Data.Sqlite;

namespace Constructd.Tests.Review;

public class FinalDeliveryTests
{
    [Fact]
    public async Task LegacyCredentialsCannotReachDelegatedRoutesAndUnenrolledHealthIsReduced()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice");
        await owner.CreateVmAsync("parent");
        var rotated = await owner.PostAsJsonAsync("/api/v1/vms/parent/token", new { kind = "legacy" });
        using var legacy = app.CreateVmTokenClient((await rotated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("vmToken").GetString()!);
        foreach (var (method, path) in new[] { ("POST", "/vms/parent/children"), ("GET", "/media"), ("POST", "/media/acquire"),
            ("POST", "/media/uploads"), ("GET", "/jobs"), ("POST", "/jobs/missing/cancel"), ("GET", "/vms/parent/addresses") })
        {
            using var request = new HttpRequestMessage(new(method), "/api/v1" + path) { Content = JsonContent.Create(new { }) };
            Assert.Equal(HttpStatusCode.Forbidden, (await legacy.SendAsync(request)).StatusCode);
        }
        using var outsider = app.CreateTestIdentityClient("not-enrolled");
        var health = await outsider.GetFromJsonAsync<JsonElement>("/api/v1/health");
        Assert.False(health.TryGetProperty("commit", out _));
    }
    [Theory]
    [InlineData("vm:parent")]
    [InlineData(" VM:parent ")]
    public async Task VmCredentialNamespaceCannotBeRegisteredAsAUser(string name)
    {
        using var app = new TestApp(); using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/users", new { name, role = "user", maxVms = 1 })).StatusCode);
        Assert.Null(await app.Users.GetAsync(name.Trim(), default));
        Assert.Contains(await app.Service<IAuditLog>().QueryAsync(10, default), a => a.Action == "user.create" && a.Outcome == AuditOutcome.Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrimaryMutationWaitsForBackgroundObservation(bool delete)
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice");
        await owner.CreateVmAsync("primary");
        var held = await app.Service<IVmOperationGate>().AcquireAsync("primary", "capacity-reconcile", default);
        var request = delete ? owner.DeleteAsync("/api/v1/vms/primary") : owner.PostAsJsonAsync("/api/v1/vms/primary/power", new { action = "stop" });
        try { await Task.Delay(50); Assert.False(request.IsCompleted); }
        finally { await held.DisposeAsync(); }
        var response = await request.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(delete ? HttpStatusCode.Accepted : HttpStatusCode.OK, response.StatusCode);
        if (delete) Assert.Equal(JobState.Succeeded, (await LifecycleTests.Finish(app, response)).State);
    }

    [Fact]
    public async Task PrimaryCreateAndStartSurviveUnavailableChildPlacementInObserveMode()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice");
        app.Service<FakeChildVmDriver>().Failure = new IOException("placement unavailable");
        var job = await owner.CreateVmAsync("primary"); Assert.Equal(JobState.Succeeded, job.State);
        (await owner.PostAsJsonAsync("/api/v1/vms/primary/power", new { action = "stop" })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync("/api/v1/vms/primary/power", new { action = "start" })).EnsureSuccessStatusCode();
        Assert.Equal(VmState.Running, app.Driver.StateOf("primary"));
    }

    [Theory]
    [InlineData(VmState.Off)]
    [InlineData(VmState.Unknown)]
    public async Task SuccessfulPrimaryStartReturnsObservedStateWithoutWaitingForRunning(VmState observed)
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice");
        await owner.CreateVmAsync("primary");
        (await owner.PostAsJsonAsync("/api/v1/vms/primary/power", new { action = "stop" })).EnsureSuccessStatusCode();
        app.Driver.StateAfterStart = observed;
        var response = await owner.PostAsJsonAsync("/api/v1/vms/primary/power", new { action = "start" }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(observed.ToString().ToLowerInvariant(), (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Empty(await app.Service<IOperationKeyStore>().ListInFlightAsync("primary", default));
        var rows = (await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations;
        if (observed == VmState.Off) Assert.StartsWith("disk:", Assert.Single(rows).Artifact);
        else Assert.Contains(rows, r => r.Resource == ReservationResource.Ram);
    }

    [Fact]
    public async Task FailedPrimaryStartReleasesRuntimeAndCompletesItsKey()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice");
        await owner.CreateVmAsync("primary");
        (await owner.PostAsJsonAsync("/api/v1/vms/primary/power", new { action = "stop" })).EnsureSuccessStatusCode();
        app.Driver.PowerFailure = new IOException("not started");
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/v1/vms/primary/power", new { action = "start" })).StatusCode);
        var disk = Assert.Single((await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations);
        Assert.StartsWith("disk:", disk.Artifact);
        Assert.Empty(await app.Service<IOperationKeyStore>().ListInFlightAsync("primary", default));
        Assert.Empty(app.Service<IOperationRegistry>().Alive());
    }

    [Fact]
    public async Task LocalUpdateTrustOverridesStaleStoredConfigurationAndCannotBeChangedByApi()
    {
        var key = Convert.ToBase64String(new byte[32]);
        using var app = new TestApp(new Dictionary<string, string?> {
            ["Constructd:HostAdmin:Updates:ManifestPublicKey"] = key,
            ["Constructd:HostAdmin:Updates:Repository"] = "trusted/construct" });
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var malicious = HostAdminDefaults.Updates with { Repository = "other/repo", ManifestPublicKey = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()) };
        await app.Service<IHostConfigStore>().SetAsync("updates", malicious, "old-config", default);
        var settings = await app.Service<PackageStager>().SettingsAsync(default);
        Assert.Equal(key, settings.ManifestPublicKey); Assert.Equal("trusted/construct", settings.Repository);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/host/config", new { updates = malicious })).StatusCode);
        var shown = await admin.GetFromJsonAsync<JsonElement>("/api/v1/host/config");
        Assert.Equal("trusted/construct", shown.GetProperty("updates").GetProperty("repository").GetString());
    }

    [Fact]
    public async Task CertFileOnlyHostRefusesUpdateBeforeDrainOrReplacement()
    {
        using var app = new TestApp();
        var options = app.Service<ConstructdOptions>(); options.Fake = false; options.CertThumbprint = null;
        var error = await Assert.ThrowsAsync<UpdateException>(() => app.Service<HostUpdateJob>().ApplyAsync("missing", "admin", default));
        Assert.Equal("update-health-pin-required", error.Code);
        Assert.Equal(MaintenanceState.Open, app.Service<IMaintenanceGate>().State);
        Assert.Equal(0, app.Service<FakeUpdaterLauncher>().LaunchCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewerBreakingSchemaRefusesStartupWhileAdditiveSchemaRemainsReadable(bool breaking)
    {
        var dir = Path.Combine(Path.GetTempPath(), "schema-review-" + Guid.NewGuid().ToString("n"));
        try
        {
            var database = new SqliteDatabase(Path.Combine(dir, "state.db")); database.EnsureCreated();
            using (var connection = database.Open())
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO schema_migrations VALUES (@id,'future',@breaking,'2026-01-01','future')";
                command.Parameters.AddWithValue("@id", SqliteMigrations.SchemaVersion + 1);
                command.Parameters.AddWithValue("@breaking", breaking ? 1 : 0); command.ExecuteNonQuery();
            }
            if (breaking) Assert.Throws<InvalidOperationException>(database.EnsureCreated);
            else database.EnsureCreated();
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task TwoUsersRaceForLastRamThroughSqlitePrimaryStartApi()
    {
        var dir = Path.Combine(Path.GetTempPath(), "start-race-" + Guid.NewGuid().ToString("n"));
        try
        {
            using var app = TestApp.WithSqlite(Path.Combine(dir, "state.db"));
            using var alice = await app.CreateUserClientAsync("alice"); using var bob = await app.CreateUserClientAsync("bob");
            foreach (var name in new[] { "alice", "bob" })
            {
                await app.Vms.AddAsync(new(name, name, 1, 1, 8, app.Clock.UtcNow, VmState.Off, null, null, IdlePolicy.Disabled, []), 5, default);
                app.Driver.SetState(name, VmState.Off);
            }
            await app.Service<IHostConfigStore>().SetAsync("capacity", HostAdminDefaults.Capacity with {
                Mode = CapacityMode.Enforce, RamHeadroomBytes = 3L << 30, StorageHeadroomBytes = 0 }, "test", default);
            app.Service<FakeHypervisorInventory>().Snapshot = new(1, app.Clock.UtcNow,
                new(8, 4L << 30, 4L << 30, [new(@"C:\", 100L << 30, 100L << 30)], app.Clock.UtcNow), [], true, []);
            var responses = await Task.WhenAll(alice.PostAsJsonAsync("/api/v1/vms/alice/power", new { action = "start" }),
                bob.PostAsJsonAsync("/api/v1/vms/bob/power", new { action = "start" }));
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
            Assert.Single(app.Driver.Calls, c => c is "start:alice" or "start:bob");
            Assert.Contains(await app.Service<IAuditLog>().QueryAsync(30, default), a => a.Action == "capacity.refuse");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }
}
