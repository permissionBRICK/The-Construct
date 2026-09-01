using System.Net;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

public class IdleApiTests
{
    [Fact]
    public async Task A_new_vm_starts_with_the_service_default_policy()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        await bob.CreateVmAsync("work-vm");

        var policy = await (await bob.GetAsync("/api/v1/vms/work-vm/idle-policy")).ReadAsync<IdlePolicyResponse>();

        Assert.Equal(120, policy.TimeoutMinutes);
        Assert.Equal(IdleAction.Save, policy.Action);
        Assert.False(policy.Clamped);
    }

    [Fact]
    public async Task A_user_sets_their_own_policy()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        await bob.CreateVmAsync("work-vm");

        var updated = await (await bob.PutJsonAsync("/api/v1/vms/work-vm/idle-policy",
            new { timeoutMinutes = 45, action = "shutdown" })).ReadAsync<IdlePolicyResponse>();

        Assert.Equal(45, updated.TimeoutMinutes);
        Assert.Equal(IdleAction.Shutdown, updated.Action);
        Assert.False(updated.Clamped);

        var reread = await (await bob.GetAsync("/api/v1/vms/work-vm/idle-policy")).ReadAsync<IdlePolicyResponse>();
        Assert.Equal(45, reread.TimeoutMinutes);
        Assert.Equal(IdleAction.Shutdown, reread.Action);
    }

    [Fact]
    public async Task The_admin_cap_clamps_a_user_policy()
    {
        var settings = new Dictionary<string, string?> { ["Constructd:Idle:MaxTimeoutMinutes"] = "60" };
        using var app = new TestApp(settings);
        using var bob = await app.CreateUserClientAsync("bob");
        await bob.CreateVmAsync("work-vm");

        var updated = await (await bob.PutJsonAsync("/api/v1/vms/work-vm/idle-policy",
            new { timeoutMinutes = 600, action = "save" })).ReadAsync<IdlePolicyResponse>();

        Assert.Equal(60, updated.TimeoutMinutes);
        Assert.Equal(60, updated.MaxTimeoutMinutes);
        Assert.True(updated.Clamped);

        var stored = await app.Vms.GetAsync("work-vm", CancellationToken.None);
        Assert.Equal(60, stored!.IdlePolicy.TimeoutMinutes);
    }

    [Fact]
    public async Task Idling_cannot_be_switched_off_when_the_admin_forces_it()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Constructd:Idle:MaxTimeoutMinutes"] = "60",
            ["Constructd:Idle:ForceEnabled"] = "true",
        };
        using var app = new TestApp(settings);
        using var bob = await app.CreateUserClientAsync("bob");
        await bob.CreateVmAsync("work-vm");

        var updated = await (await bob.PutJsonAsync("/api/v1/vms/work-vm/idle-policy",
            new { timeoutMinutes = 0, action = "off" })).ReadAsync<IdlePolicyResponse>();

        Assert.Equal(60, updated.TimeoutMinutes);
        Assert.Equal(IdleAction.Save, updated.Action);
        Assert.True(updated.Clamped);
    }

    [Theory]
    [InlineData(-1, "save", "timeoutMinutes")]
    [InlineData(null, "save", "timeoutMinutes")]
    [InlineData(30, "hibernate", "action")]
    [InlineData(30, null, "action")]
    public async Task Invalid_policies_are_rejected(int? timeoutMinutes, string? action, string expected)
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        await bob.CreateVmAsync("work-vm");

        using var response = await bob.PutJsonAsync("/api/v1/vms/work-vm/idle-policy",
            new { timeoutMinutes, action });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_initial_policy_can_be_passed_at_creation_time()
    {
        var settings = new Dictionary<string, string?> { ["Constructd:Idle:MaxTimeoutMinutes"] = "90" };
        using var app = new TestApp(settings);
        using var bob = await app.CreateUserClientAsync("bob");

        await bob.CreateVmAsync("work-vm");
        var jobId = await bob.StartCreateVmAsync("other-vm", opts: new { idlePolicy = new { timeoutMinutes = 600, action = "shutdown" } });
        await bob.WaitForJobAsync(jobId);

        var policy = await (await bob.GetAsync("/api/v1/vms/other-vm/idle-policy")).ReadAsync<IdlePolicyResponse>();

        Assert.Equal(90, policy.TimeoutMinutes);
        Assert.Equal(IdleAction.Shutdown, policy.Action);
    }

    [Fact]
    public async Task The_guest_posts_its_heartbeat_with_the_vm_token()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        var job = await bob.CreateVmAsync("work-vm");
        using var guest = app.CreateVmTokenClient(job.VmToken());

        using var response = await guest.PostJsonAsync("/api/v1/vms/work-vm/activity",
            new { busy = true, reasons = new[] { "claude running", "tmux output" } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var report = await app.Vms.GetLatestActivityAsync("work-vm", CancellationToken.None);
        Assert.NotNull(report);
        Assert.True(report!.Busy);
        Assert.Equal(new[] { "claude running", "tmux output" }, report.Reasons.ToArray());
    }

    [Fact]
    public async Task A_heartbeat_needs_the_right_vm_token()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        var job = await bob.CreateVmAsync("work-vm");
        await bob.CreateVmAsync("other-vm");
        using var guest = app.CreateVmTokenClient(job.VmToken());

        using var response = await guest.PostJsonAsync("/api/v1/vms/other-vm/activity", new { busy = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_heartbeat_without_busy_is_rejected()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        var job = await bob.CreateVmAsync("work-vm");
        using var guest = app.CreateVmTokenClient(job.VmToken());

        using var response = await guest.PostJsonAsync("/api/v1/vms/work-vm/activity", new { reasons = new[] { "x" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// End to end through the composed host: a heartbeat posted over HTTP is what keeps the engine
    /// from saving the VM, and its absence is what makes the engine act.
    /// </summary>
    [Fact]
    public async Task The_heartbeat_decides_what_the_idle_engine_does()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        var job = await bob.CreateVmAsync("work-vm");
        using var guest = app.CreateVmTokenClient(job.VmToken());
        await bob.PutJsonAsync("/api/v1/vms/work-vm/idle-policy", new { timeoutMinutes = 60, action = "save" });

        var engine = app.Service<IIdlePolicyEngine>();
        var start = app.Clock.UtcNow;

        await engine.EvaluateAsync(start, CancellationToken.None);

        // Two hours later the guest reports it is busy: the VM stays up even with zero connections.
        app.Clock.UtcNow = start.AddHours(2);
        await guest.PostJsonAsync("/api/v1/vms/work-vm/activity", new { busy = true, reasons = new[] { "agent job" } });
        var busyOutcome = Assert.Single(await engine.EvaluateAsync(app.Clock.UtcNow, CancellationToken.None));
        Assert.Equal(IdleDecisionKind.KeepAlive, busyOutcome.Decision.Kind);
        Assert.Equal(VmState.Running, app.Driver.StateOf("work-vm"));

        // Two hours after the last busy report, with nobody connected: saved, and audited.
        app.Clock.UtcNow = start.AddHours(4);
        await guest.PostJsonAsync("/api/v1/vms/work-vm/activity", new { busy = false, reasons = Array.Empty<string>() });
        var idleOutcome = Assert.Single(await engine.EvaluateAsync(app.Clock.UtcNow, CancellationToken.None));
        Assert.Equal(IdleDecisionKind.Save, idleOutcome.Decision.Kind);
        Assert.True(idleOutcome.Applied);
        Assert.Equal(VmState.Saved, app.Driver.StateOf("work-vm"));

        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var entries = await (await admin.GetAsync("/api/v1/audit")).ReadAsync<List<AuditResponse>>();
        Assert.Contains(entries, e => e is { Action: "vm.idle-save", Target: "work-vm", Actor: "system" });
    }

    [Fact]
    public async Task Idle_policy_changes_are_audited()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await admin.CreateVmAsync("work-vm");

        await admin.PutJsonAsync("/api/v1/vms/work-vm/idle-policy", new { timeoutMinutes = 15, action = "save" });

        var entries = await (await admin.GetAsync("/api/v1/audit")).ReadAsync<List<AuditResponse>>();
        Assert.Contains(entries, e => e is { Action: "vm.idle-policy", Target: "work-vm" });
    }
}
