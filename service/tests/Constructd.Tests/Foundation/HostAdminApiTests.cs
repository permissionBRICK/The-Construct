using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Tests.Support;
namespace Constructd.Tests.Foundation;

public sealed class HostAdminApiTests
{
    public static IEnumerable<object[]> AdminRoutes => new[] {
        ("GET","/host/status"),("GET","/host/config"),("PUT","/host/config"),("GET","/users"),("GET","/users/alice"),
        ("PUT","/users/alice"),("GET","/users/alice/allowance"),("PUT","/users/alice/allowance"),
        ("GET","/users/alice/tokens"),("DELETE","/users/alice/tokens/missing"),("GET","/vms/parent/overrides"),
        ("PUT","/vms/parent/overrides"),("DELETE","/vms/parent/overrides") }.Select(x => new object[] { x.Item1, x.Item2 });
    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task AdminInventoryAndPolicyRequireAdmin(string method, string path)
    {
        using var app = new TestApp(); using var user = await app.CreateUserClientAsync("alice"); using var anonymous = app.CreateAnonymousClient();
        using var request = new HttpRequestMessage(new(method), "/api/v1" + path) { Content = JsonContent.Create(new { }) };
        Assert.Equal(HttpStatusCode.Forbidden, (await user.SendAsync(request)).StatusCode);
        using var noAuth = new HttpRequestMessage(new(method), "/api/v1" + path) { Content = JsonContent.Create(new { }) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(noAuth)).StatusCode);
    }
    [Fact]
    public async Task DiscoveryIsReducedAnonymouslyAndLiveForUsers()
    {
        using var app = new TestApp(); using var anonymous = app.CreateAnonymousClient(); using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var health = await anonymous.GetFromJsonAsync<JsonElement>("/api/v1/health");
        Assert.Equal("ok", health.GetProperty("status").GetString()); Assert.Equal(100, health.GetProperty("schemaVersion").GetInt32());
        Assert.False(health.TryGetProperty("commit", out _)); Assert.False(health.TryGetProperty("packageVersion", out _));
        var full = await admin.GetFromJsonAsync<JsonElement>("/api/v1/health"); Assert.True(full.TryGetProperty("commit", out _));
        var who = await (await admin.GetAsync("/api/v1/whoami")).ReadAsync<WhoAmIResponse>(); Assert.True(who.Enabled); Assert.NotNull(who.Effective); Assert.Contains("host-admin", who.ApiFeatures!);
        var status = await admin.GetFromJsonAsync<JsonElement>("/api/v1/host/status"); Assert.Equal("observe", status.GetProperty("capacityMode").GetString());
        Assert.False(status.GetProperty("capacity").GetProperty("complete").GetBoolean()); Assert.Equal(JsonValueKind.Array, status.GetProperty("activeJobs").ValueKind);
        var caps = await admin.GetFromJsonAsync<JsonElement>("/api/v1/host/capabilities"); Assert.Equal("fake", caps.GetProperty("backend").GetString());
        Assert.Equal("supported", caps.GetProperty("capabilities").GetProperty("console").GetProperty("screenshot").GetString());
    }
    [Fact]
    public async Task UserRoutesEditPolicyAndRolesTakeEffectOnExistingCredentials()
    {
        using var app = new TestApp(); using var admin = await app.CreateUserClientAsync("admin", Role.Admin); using var other = await app.CreateUserClientAsync("other", Role.Admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/users")).StatusCode);
        var detail = await other.GetFromJsonAsync<JsonElement>("/api/v1/users/other"); Assert.True(detail.GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/users/other", new { role = "user", maxVms = 7 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/v1/users/admin", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/users/other/allowance", new { maxRetainedChildren = 5, allowSharing = false })).StatusCode);
        var allowance = await admin.GetFromJsonAsync<JsonElement>("/api/v1/users/other/allowance"); Assert.Equal(5, allowance.GetProperty("effective").GetProperty("maxRetainedChildren").GetInt32());
        var who = await (await other.GetAsync("/api/v1/whoami")).ReadAsync<WhoAmIResponse>(); Assert.Equal(5, who.Effective!.MaxRetainedChildren); Assert.False(who.Effective.AllowSharing);
        var tokens = await admin.GetFromJsonAsync<JsonElement>("/api/v1/users/other/tokens"); var id = tokens[0].GetProperty("id").GetString();
        Assert.DoesNotContain("hash", tokens.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/v1/users/other/tokens/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/whoami")).StatusCode);
    }
    [Fact]
    public async Task ConfigValidatesAllSectionsAndRejectsStaleWrites()
    {
        using var app = new TestApp(); using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var initial = await admin.GetFromJsonAsync<JsonElement>("/api/v1/host/config"); Assert.Equal("default", initial.GetProperty("capacity").GetProperty("source").GetString());
        var invalid = await admin.PutAsJsonAsync("/api/v1/host/config", new { network = new { hostForwardsEnabled = false, directAddressReporting = true }, lifecycle = new { gracefulShutdownTimeoutSeconds = 1, leaseTickSeconds = 30, leaseRetrySeconds = 600 } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); Assert.Equal("validation", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Null(await app.Service<IHostConfigStore>().GetAsync<NetworkConfig>("network", default));
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/host/config", new { network = new { hostForwardsEnabled = false, directAddressReporting = true, expectedUpdatedAt = (string?)null } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/v1/host/config", new { network = new { hostForwardsEnabled = true, directAddressReporting = true, expectedUpdatedAt = (string?)null } })).StatusCode);
        var after = await admin.GetFromJsonAsync<JsonElement>("/api/v1/host/config"); Assert.Equal("stored", after.GetProperty("network").GetProperty("source").GetString()); Assert.False(after.GetProperty("network").GetProperty("hostForwardsEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/host/config", new { updates = new { repository = "bad", channel = "main", drainTimeoutMinutes = 60, healthTimeoutSeconds = 120, requireSignature = true } })).StatusCode);
    }
    [Fact]
    public async Task NewPrimaryAndRotationDiscoveryRespectKindAndRevocation()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice"); var job = await owner.CreateVmAsync("parent");
        Assert.Equal(VmTokenKind.Primary, (await app.Vms.GetAsync("parent", default))!.TokenKind);
        using var first = app.CreateVmTokenClient(job.VmToken()); Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/v1/vms")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/v1/vms/parent")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await first.PostAsJsonAsync("/api/v1/vms/parent/token", new { })).StatusCode);
        var rotated = await owner.PostAsJsonAsync("/api/v1/vms/parent/token", new { kind = "legacy" }); Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var body = await rotated.Content.ReadFromJsonAsync<JsonElement>(); using var legacy = app.CreateVmTokenClient(body.GetProperty("vmToken").GetString()!);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/v1/vms/parent/identity")).StatusCode);
        var identity = await legacy.GetFromJsonAsync<JsonElement>("/api/v1/vms/parent/identity"); Assert.Equal("legacy", identity.GetProperty("tokenKind").GetString()); Assert.Equal(JsonValueKind.Null, identity.GetProperty("delegation").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await legacy.GetAsync("/api/v1/host/capabilities")).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await legacy.GetAsync("/api/v1/vms")).StatusCode);
        var userIdentity = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/parent/identity"); Assert.Equal(JsonValueKind.Null, userIdentity.GetProperty("tokenKind").ValueKind);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync("/api/v1/vms/parent/token")).StatusCode); Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync("/api/v1/vms/parent/forwards")).StatusCode);
    }
    [Fact]
    public async Task GuestReportEventsDoNotOverwriteEachOtherAndBootIsNotProvisioning()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice"); var job = await owner.CreateVmAsync("parent"); using var guest = app.CreateVmTokenClient(job.VmToken());
        var at = app.Clock.UtcNow;
        Assert.Equal(HttpStatusCode.OK, (await guest.PostAsJsonAsync("/api/v1/vms/parent/guest-report", new { @event = "provisioned", constructCommit = "abcdef0", at, reporter = "Provision-AgentVM.ps1" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.PostAsJsonAsync("/api/v1/vms/parent/guest-report", new { @event = "reinstalled", at = at.AddHours(1), reporter = "provision.sh" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.PostAsJsonAsync("/api/v1/vms/parent/guest-report", new { @event = "attempt", outcome = "failed", constructCommit = "1111111", reporter = "Provision-AgentVM.ps1" })).StatusCode);
        var vm = (await app.Vms.GetAsync("parent", default))!; Assert.Equal(at, vm.Guest!.ProvisionedAt); Assert.Equal(at.AddHours(1), vm.Guest.ReinstalledAt); Assert.Equal("abcdef0", vm.Guest.ConstructCommit); Assert.Equal("failed", vm.Guest.LastAttemptOutcome);
        var child = vm with { Name = "child", Kind = VmKind.Child, Parent = "parent", VmTokenHash = null, Guest = null, Observed = null, RamMb = 512, Hardware = new(1, 512, 10, 2, false, null, false, [], true), Lease = new("never", null, null, null, LeaseState.Inactive, 0, null, null) };
        var store = app.Service<IVmDelegationRepository>(); await store.AddAsync(child, new(5, true, 2, null, null, null, null, true, true, true), default);
        await store.UpdateObservationAsync("child", new(at, at, [], null), default);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/v1/vms/child/guest-report", new { @event = "provisioned", reporter = "provision.sh" })).StatusCode);
        var inventory = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/child"); Assert.Equal(JsonValueKind.Null, inventory.GetProperty("guest").GetProperty("provisionedAt").ValueKind); Assert.Equal("unknown", inventory.GetProperty("guest").GetProperty("provenance").GetString());
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync("/api/v1/vms/parent/children")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/v1/vms/child/token", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.GetAsync("/api/v1/vms/child/endpoint")).StatusCode);
        var caps = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/child/capabilities"); Assert.Equal("conditional", caps.GetProperty("console").GetProperty("screenshot").GetString());
    }
    [Fact]
    public async Task OverridesOnlyRestrictAndAreImmediatelyVisible()
    {
        using var app = new TestApp(); using var admin = await app.CreateUserClientAsync("admin", Role.Admin); using var owner = await app.CreateUserClientAsync("alice"); var job = await owner.CreateVmAsync("parent"); using var guest = app.CreateVmTokenClient(job.VmToken());
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/users/alice/allowance", new { maxRetainedChildren = 4, allowSharing = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/vms/parent/overrides", new { maxRetainedChildren = 2, allowSharing = true })).StatusCode);
        var stored = await admin.GetFromJsonAsync<JsonElement>("/api/v1/vms/parent/overrides"); Assert.Equal(2, stored.GetProperty("effective").GetProperty("maxRetainedChildren").GetInt32()); Assert.False(stored.GetProperty("effective").GetProperty("allowSharing").GetBoolean());
        var identity = await guest.GetFromJsonAsync<JsonElement>("/api/v1/vms/parent/identity"); Assert.Equal(2, identity.GetProperty("delegation").GetProperty("maxRetainedChildren").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/v1/vms/parent/overrides")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/vms/shared")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/users/alice", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/v1/whoami")).StatusCode); Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/v1/vms/parent/identity")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedInventoryAndCurrentPolicyWorkWithBothStores(bool sqlite)
    {
        var dir = Path.Combine(Path.GetTempPath(), "foundation-api-" + Guid.NewGuid().ToString("n")); Directory.CreateDirectory(dir);
        try
        {
            using var app = sqlite ? TestApp.WithSqlite(Path.Combine(dir, "test.db")) : new TestApp();
            using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
            using var owner = await app.CreateUserClientAsync("alice"); using var other = await app.CreateUserClientAsync("bob");
            var job = await owner.CreateVmAsync("parent"); using var guest = app.CreateVmTokenClient(job.VmToken());
            var primary = (await app.Vms.GetAsync("parent", default))!;
            var child = primary with
            {
                Name = "shared",
                Kind = VmKind.Child,
                Parent = "parent",
                Sharing = SharingScope.Host,
                VmTokenHash = null,
                Guest = null,
                RamMb = 512,
                Hardware = new(1, 512, 10, 2, false, null, false, [], true),
                Lease = new("never", null, null, null, LeaseState.Inactive, 0, null, null)
            };
            Assert.Equal(VmAddDecision.Added, await app.Service<IVmDelegationRepository>().AddAsync(child, new(5, true, 5, null, null, null, null, true, true, true), default));
            Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/v1/vms/shared")).StatusCode);
            var shared = await other.GetFromJsonAsync<JsonElement>("/api/v1/vms/shared"); Assert.Single(shared.EnumerateArray());
            var adminShared = await admin.GetFromJsonAsync<JsonElement>("/api/v1/vms/shared"); Assert.Single(adminShared.EnumerateArray()); Assert.True(adminShared[0].GetProperty("shared").GetBoolean());
            Assert.Empty((await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/shared")).EnumerateArray());
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/v1/vms/parent/children")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync("/api/v1/vms/shared/token", new { })).StatusCode);
            var listed = await other.GetFromJsonAsync<JsonElement>("/api/v1/vms?kind=child"); Assert.Empty(listed.EnumerateArray());
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/host/config", new { network = new { hostForwardsEnabled = false, directAddressReporting = true } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostAsJsonAsync("/api/v1/vms/parent/forwards", new { vmPort = 8080, target = "host" })).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await guest.PostAsJsonAsync("/api/v1/vms/parent/forwards", new { vmPort = 8080, target = "client" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/users/alice", new { enabled = false })).StatusCode);
            Assert.Empty((await other.GetFromJsonAsync<JsonElement>("/api/v1/vms/shared")).EnumerateArray());
            Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/v1/vms/parent/identity")).StatusCode);
            var audit = await app.Service<IAuditLog>().QueryAsync(100, default);
            Assert.Contains(audit, a => a.Action == "host.config" && a.Outcome == AuditOutcome.Success);
            Assert.Contains(audit, a => a.Action == "user.update" && a.Outcome == AuditOutcome.Success);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task FailedPrimaryRemovalCanStillBeRetriedWithItsFenceIntact()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice");
        await owner.CreateVmAsync("parent");
        app.Forwards.Failure = new InvalidOperationException("fixture");
        var first = await owner.DeleteAsync("/api/v1/vms/parent");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(JobState.Failed, (await owner.WaitForJobAsync((await first.ReadAsync<JobAcceptedResponse>()).JobId)).State);
        Assert.True((await app.Vms.GetAsync("parent", default))!.Deleting);
        app.Forwards.Failure = null;
        var retry = await owner.DeleteAsync("/api/v1/vms/parent");
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal(JobState.Succeeded, (await owner.WaitForJobAsync((await retry.ReadAsync<JobAcceptedResponse>()).JobId)).State);
        Assert.Null(await app.Vms.GetAsync("parent", default));
    }
}
