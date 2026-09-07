using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Hosting;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Tests.Support;
namespace Constructd.Tests.Delegation;

public sealed class LifecycleTests
{
    internal static async Task<HttpClient> Setup(TestApp app, bool start = true)
    {
        var client = await app.CreateUserClientAsync("alice");
        await app.Vms.AddAsync(new("parent", "alice", 1, 1, 1, app.Clock.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, []), 5, default);
        await app.Service<IUserAllowanceStore>().SetAllowanceAsync("alice", UserAllowance.Unset with
            { AllowChildCreation = true, MaxRetainedChildren = 5, AllowNeverLifetime = true, AllowSharing = true }, default);
        if (app.Service<ICapacityLedger>() is InMemoryCapacityLedger memory) memory.Mode = CapacityMode.Observe;
        await app.Service<IMediaStore>().AddAsync(new("install", "alice", "install.iso", MediaRole.Install, MediaSource.Upload, null,
            @"C:\media\install.iso", MediaState.Ready, 40960, 40960, null, null, null, null, null, app.Clock.UtcNow, app.Clock.UtcNow, null), default);
        Assert.Equal(JobState.Succeeded, (await Finish(app, await client.PostAsJsonAsync("/api/v1/vms/parent/children", Request("child", start)))).State);
        return client;
    }
    internal static object Request(string name, bool start = false) => new { name, cpus = 1, ramMb = 512, diskGb = 1, lifetime = "10m", media = new { installMediaId = "install" }, preset = "windows", start };
    internal static async Task<Job> Finish(TestApp app, HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return await Finish(app, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!);
    }
    internal static async Task<Job> Finish(TestApp app, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var engine = app.Service<IJobEngine>();
        await foreach (var unused in engine.SubscribeAsync(id, timeout.Token)) { }
        return (await engine.GetAsync(id, default))!;
    }
    public static IEnumerable<object[]> Matrix()
    {
        foreach (var actor in new[] { "owner", "admin", "parent", "shared-user", "shared-primary", "stranger", "legacy" })
            foreach (var op in new[] { "inspect", "start", "restart", "shutdown", "save", "share", "renew", "delete", "hardware", "media" })
                yield return [actor, op];
    }
    [Theory, MemberData(nameof(Matrix))]
    public async Task ActorOperationMatrix(string actor, string op)
    {
        await using var app = new TestApp(); using var owner = await Setup(app, op is not ("start" or "hardware" or "media"));
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        using var other = await app.CreateUserClientAsync("bob");
        await app.Vms.AddAsync(new("other", "bob", 1, 1, 1, app.Clock.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, []), 5, default);
        var issuer = app.Service<IVmTokenIssuer>();
        using var parent = app.CreateVmTokenClient(await issuer.IssueVmTokenAsync("parent", VmTokenKind.Primary, default));
        using var primary = app.CreateVmTokenClient(await issuer.IssueVmTokenAsync("other", actor == "legacy" ? VmTokenKind.Legacy : VmTokenKind.Primary, default));
        if (actor.StartsWith("shared", StringComparison.Ordinal))
            (await owner.PutAsJsonAsync("/api/v1/vms/child/sharing", new { scope = "host" })).EnsureSuccessStatusCode();
        var caller = actor switch { "owner" => owner, "admin" => admin, "parent" => parent, "shared-primary" or "legacy" => primary, _ => other };
        using var response = op switch
        {
            "hardware" => await caller.PutAsJsonAsync("/api/v1/vms/child/hardware", new { ramMb = 1024 }),
            "media" => await caller.PutAsJsonAsync("/api/v1/vms/child/media", new { installMediaId = "install" }),
            "inspect" => await caller.GetAsync("/api/v1/vms/child"),
            "share" => await caller.PutAsJsonAsync("/api/v1/vms/child/sharing", new { scope = "host" }),
            "renew" => await caller.PostAsJsonAsync("/api/v1/vms/child/lease", new { lifetime = "20m" }),
            "delete" => await caller.DeleteAsync("/api/v1/vms/child"),
            _ => await caller.PostAsJsonAsync("/api/v1/vms/child/lifecycle", new { action = op, lifetime = op == "start" ? "20m" : null })
        };
        var allowed = actor is "owner" or "admin" or "parent" || actor.StartsWith("shared", StringComparison.Ordinal) && op is "inspect" or "start" or "restart" or "shutdown" or "save";
        Assert.Equal(allowed, response.IsSuccessStatusCode);
        if (response.StatusCode == HttpStatusCode.Accepted) Assert.Equal(JobState.Succeeded, (await Finish(app, response)).State);
        if (!allowed) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
    [Fact]
    public async Task PrimaryTokenCreatesChildrenWithCurrentOwnerPolicyAndNoCredential()
    {
        await using var app = new TestApp(); using var owner = await Setup(app, false);
        using var token = app.CreateVmTokenClient(await app.Service<IVmTokenIssuer>().IssueVmTokenAsync("parent", VmTokenKind.Primary, default));
        var job = await Finish(app, await token.PostAsJsonAsync("/api/v1/vms/parent/children", Request("second")));
        Assert.Equal(JobState.Succeeded, job.State); Assert.Equal("vm:parent", job.Initiator);
        Assert.Null((await app.Vms.GetAsync("second", default))!.VmTokenHash);
        Assert.Equal(HttpStatusCode.OK, (await token.GetAsync("/api/v1/jobs/" + job.Id)).StatusCode);
        await app.Service<IUserAllowanceStore>().SetAllowanceAsync("alice", UserAllowance.Unset with { AllowChildCreation = false }, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.PostAsJsonAsync("/api/v1/vms/parent/children", Request("third"))).StatusCode);
    }
    [Theory]
    [InlineData("never")]
    [InlineData("30m")]
    public async Task SharedStartUsesOwnersLifetimeNotCallers(string lifetime)
    {
        await using var app = new TestApp(); using var owner = await Setup(app, false); using var bob = await app.CreateUserClientAsync("bob");
        (await owner.PutAsJsonAsync("/api/v1/vms/child/sharing", new { scope = "host" })).EnsureSuccessStatusCode();
        await app.Service<IUserAllowanceStore>().SetAllowanceAsync("alice", UserAllowance.Unset with { AllowSharing = true, MaxChildLifetimeSeconds = 600, AllowNeverLifetime = false }, default);
        var response = await bob.PostAsJsonAsync("/api/v1/vms/child/lifecycle", new { action = "start", lifetime });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Contains("lifetime-not-allowed", await response.Content.ReadAsStringAsync());
        Assert.Equal(VmState.Off, app.Driver.StateOf("child"));
    }
    [Fact]
    public async Task SaveResumeRenewAndRestartHaveDistinctLeaseSemantics()
    {
        await using var app = new TestApp(); using var client = await Setup(app);
        var original = (await app.Vms.GetAsync("child", default))!.Lease;
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(JobState.Succeeded, (await Finish(app, await client.PostAsJsonAsync("/api/v1/vms/child/lifecycle", new { action = "restart" }))).State);
        Assert.Equal(original, (await app.Vms.GetAsync("child", default))!.Lease);
        (await client.PostAsJsonAsync("/api/v1/vms/child/lifecycle", new { action = "save" })).EnsureSuccessStatusCode();
        Assert.Equal(original, (await app.Vms.GetAsync("child", default))!.Lease);
        Assert.All((await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations, row => Assert.Equal(ReservationResource.Storage, row.Resource));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/vms/child/lifecycle", new { action = "start" })).StatusCode);
        (await client.PostAsJsonAsync("/api/v1/vms/child/lifecycle", new { action = "start", lifetime = "20m", operationKey = "resume-1" })).EnsureSuccessStatusCode();
        var resumed = (await app.Vms.GetAsync("child", default))!.Lease!;
        Assert.Equal(app.Clock.UtcNow.AddMinutes(20), resumed.ExpiresAt);
        app.Clock.Advance(TimeSpan.FromMinutes(2));
        (await client.PostAsJsonAsync("/api/v1/vms/child/lifecycle", new { action = "start", lifetime = "20m", operationKey = "resume-1" })).EnsureSuccessStatusCode();
        Assert.Equal(resumed, (await app.Vms.GetAsync("child", default))!.Lease);
        (await client.PostAsJsonAsync("/api/v1/vms/child/lease", new { lifetime = "30m", operationKey = "renew-one" })).EnsureSuccessStatusCode();
        Assert.Equal(app.Clock.UtcNow.AddMinutes(30), (await app.Vms.GetAsync("child", default))!.Lease!.ExpiresAt);
    }
    [Theory]
    [InlineData(GracefulShutdownOutcome.Completed, LeaseState.Expired)]
    [InlineData(GracefulShutdownOutcome.Unavailable, LeaseState.Overdue)]
    [InlineData(GracefulShutdownOutcome.Timeout, LeaseState.Overdue)]
    public async Task ExpiryNeverForcesOrDeletesAndRetriesWithOriginalDeadline(GracefulShutdownOutcome outcome, LeaseState expected)
    {
        await using var app = new TestApp(); using var client = await Setup(app);
        var lease = (await app.Vms.GetAsync("child", default))!.Lease!;
        app.Service<FakeChildVmDriver>().ShutdownOutcome = outcome;
        app.Clock.Advance(TimeSpan.FromHours(3));
        var scheduler = app.Service<LeaseSchedulerService>();
        var id = Assert.Single(await scheduler.TickAsync(default)); var job = await Finish(app, id);
        var after = (await app.Vms.GetAsync("child", default))!.Lease!;
        Assert.Equal(expected, after.State); Assert.Equal(lease.ExpiresAt, after.ExpiresAt); Assert.Equal(lease.ActivatedAt, after.ActivatedAt);
        Assert.Equal(outcome == GracefulShutdownOutcome.Completed ? JobState.Succeeded : JobState.Failed, job.State);
        Assert.DoesNotContain(app.Driver.Calls, c => c.StartsWith("stop:", StringComparison.Ordinal) || c.StartsWith("remove:", StringComparison.Ordinal));
        Assert.Contains((await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations, r => r.Resource == ReservationResource.Storage);
        Assert.Empty(await scheduler.TickAsync(default));
        if (expected == LeaseState.Overdue)
        {
            app.Clock.Advance(TimeSpan.FromMinutes(10)); app.Service<FakeChildVmDriver>().ShutdownOutcome = GracefulShutdownOutcome.Completed;
            Assert.Equal(JobState.Succeeded, (await Finish(app, Assert.Single(await scheduler.TickAsync(default)))).State);
            Assert.Equal(LeaseState.Expired, (await app.Vms.GetAsync("child", default))!.Lease!.State);
        }
    }
    [Fact]
    public async Task InactiveNeverExpiresUntilExternalStartWhichIsImmediatelyOverdue()
    {
        await using var app = new TestApp(); using var client = await Setup(app, false);
        app.Clock.Advance(TimeSpan.FromDays(30)); var scheduler = app.Service<LeaseSchedulerService>();
        Assert.Empty(await scheduler.TickAsync(default));
        app.Driver.SetState("child", VmState.Running);
        await app.Service<IChildLeaseReconciler>().ReconcileAsync((await app.Vms.GetAsync("child", default))!, VmState.Running, default);
        await Finish(app, Assert.Single(await scheduler.TickAsync(default)));
        Assert.Equal(LeaseState.Expired, (await app.Vms.GetAsync("child", default))!.Lease!.State);
        Assert.Equal(VmState.Off, app.Driver.StateOf("child"));
    }
    [Fact]
    public async Task RenewedLeaseSupersedesQueuedExpiry()
    {
        await using var app = new TestApp(); using var client = await Setup(app);
        var before = (await app.Vms.GetAsync("child", default))!;
        app.Clock.Advance(TimeSpan.FromMinutes(11));
        (await client.PostAsJsonAsync("/api/v1/vms/child/lease", new { lifetime = "30m" })).EnsureSuccessStatusCode();
        var job = new Job("old-expiry", "vm-shutdown", "child", "alice", JobState.Running, [], null, null, app.Clock.UtcNow, null);
        var result = await app.Service<ChildLifecycleJobs>().RunAsync(job, false, before.Lease!.Version, new Progress<string>(), default);
        Assert.Contains("superseded", JsonSerializer.Serialize(result)); Assert.Equal(VmState.Running, app.Driver.StateOf("child"));
    }
    [Fact]
    public async Task PrivateRevocationRemovesSharedSessionsAndDeniesNewCalls()
    {
        await using var app = new TestApp(); using var owner = await Setup(app); using var bob = await app.CreateUserClientAsync("bob");
        (await owner.PutAsJsonAsync("/api/v1/vms/child/sharing", new { scope = "host" })).EnsureSuccessStatusCode();
        var sessions = app.Service<IConsoleSessionStore>();
        var own = sessions.TryCreate("child", "alice", 100, 100, TimeSpan.FromMinutes(5), app.Clock.UtcNow)!;
        var shared = sessions.TryCreate("child", "bob", 100, 100, TimeSpan.FromMinutes(5), app.Clock.UtcNow)!;
        (await owner.PutAsJsonAsync("/api/v1/vms/child/sharing", new { scope = "private" })).EnsureSuccessStatusCode();
        Assert.NotNull(sessions.Get(own.Id, app.Clock.UtcNow)); Assert.Null(sessions.Get(shared.Id, app.Clock.UtcNow));
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostAsJsonAsync("/api/v1/vms/child/lifecycle", new { action = "save" })).StatusCode);
    }
    [Fact]
    public async Task FailedStartCanBeSupersededByANewOperationWithoutDuplicateRuntime()
    {
        await using var app = new TestApp(); using var client = await Setup(app, false);
        app.Driver.PowerFailure = new InvalidOperationException("private failure");
        var failed = await client.PostAsJsonAsync("/api/v1/vms/child/lifecycle", new { action = "start", lifetime = "10m", operationKey = "first-attempt" });
        Assert.Contains("start-failed", await failed.Content.ReadAsStringAsync());
        app.Driver.PowerFailure = null;
        (await client.PostAsJsonAsync("/api/v1/vms/child/lifecycle", new { action = "start", lifetime = "20m", operationKey = "second-attempt" })).EnsureSuccessStatusCode();
        var old = await client.PostAsJsonAsync("/api/v1/vms/child/lifecycle", new { action = "start", lifetime = "10m", operationKey = "first-attempt" });
        Assert.Contains("superseded", await old.Content.ReadAsStringAsync());
        var rows = (await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations;
        Assert.Single(rows, r => r.Resource == ReservationResource.Ram); Assert.Single(rows, r => r.Resource == ReservationResource.Cpu);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignCallerCannotProbeParentPolicy(bool token)
    {
        await using var app = new TestApp(); using var owner = await Setup(app); using var user = await app.CreateUserClientAsync("bob");
        await app.Vms.AddAsync(new("foreign", "bob", 1, 1, 1, app.Clock.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, []), 5, default);
        using var primary = app.CreateVmTokenClient(await app.Service<IVmTokenIssuer>().IssueVmTokenAsync("foreign", VmTokenKind.Primary, default));
        await app.Service<IUserAllowanceStore>().SetAllowanceAsync("alice", UserAllowance.Unset with { AllowChildCreation = false }, default);
        var response = await (token ? primary : user).PostAsJsonAsync("/api/v1/vms/parent/children", Request("probe"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("delegation-disabled", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("parent-closed", await response.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task SavingAnOffChildIsACodedRefusalBeforeTheDriverCall()
    {
        await using var app = new TestApp(); using var client = await Setup(app, false);
        var response = await client.PostAsJsonAsync("/api/v1/vms/child/lifecycle", new { action = "save" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("save-requires-running-or-paused", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("save:child", app.Driver.Calls);
    }
}
