using System.Net;
using Constructd.Api.Contracts;
using Constructd.Core.Domain;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

public class VmLifecycleTests
{
    [Fact]
    public async Task Creating_a_vm_runs_the_whole_job_and_returns_the_endpoint_and_vm_token()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");

        var jobId = await bob.StartCreateVmAsync("work-vm");
        var job = await bob.WaitForJobAsync(jobId);

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(JobKinds.CreateVm, job.Kind);
        Assert.Equal("work-vm", job.VmName);

        var endpoint = job.ResultElement("endpoint");
        Assert.Equal("buildbox.test", endpoint.GetProperty("sshHost").GetString());
        Assert.Equal(2201, endpoint.GetProperty("sshPort").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(job.VmTokenOrNull()));

        // The steps of plan §4.4 ran in order, and their progress was recorded.
        var log = string.Join("\n", job.Progress.Select(p => p.Text));
        Assert.Contains("building autoinstall ISO", log);
        Assert.Contains("creating vm work-vm", log);
        Assert.Contains("answered ssh", log);
        Assert.Contains("ssh forward allocated", log);
        Assert.Contains("vm-scoped token issued", log);
        Assert.Equal(
            new[] { "create:work-vm", "wait:work-vm", "detach:work-vm" },
            app.Driver.Calls.Take(3).ToArray());

        var vm = await (await bob.GetAsync("/api/v1/vms/work-vm")).ReadAsync<VmResponse>();
        Assert.Equal(VmState.Running, vm.State);
        Assert.Equal(2201, vm.SshForwardPort);
        Assert.Equal("bob", vm.Owner);
    }

    [Fact]
    public async Task The_job_event_stream_replays_progress_and_ends_with_a_state_event()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");

        var jobId = await bob.StartCreateVmAsync("work-vm");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await bob.GetAsync($"/api/v1/jobs/{jobId}/events", cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(cts.Token);

        Assert.Contains("event: progress", body);
        Assert.Contains("building autoinstall ISO", body);
        Assert.Contains("event: state", body);
        Assert.Contains("\"state\":\"succeeded\"", body);

        // Exactly one terminal event, and it is the last one.
        Assert.Equal(1, body.Split("event: state").Length - 1);
        Assert.EndsWith("\n\n", body);
        Assert.True(body.LastIndexOf("event: state", StringComparison.Ordinal) >
                    body.LastIndexOf("event: progress", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failing_creation_fails_the_job_and_frees_the_name_again()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob", maxVms: 1);
        app.Driver.CreateFailure = new InvalidOperationException("hyper-v said no");

        var job = await bob.WaitForJobAsync(await bob.StartCreateVmAsync("work-vm"));

        Assert.Equal(JobState.Failed, job.State);

        // The driver's message is not repeated; only its type is (see SafeError).
        Assert.Equal("InvalidOperationException", job.Error);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/api/v1/vms/work-vm")).StatusCode);

        // The quota slot and the name were released, so a retry works.
        app.Driver.CreateFailure = null;
        var retry = await bob.WaitForJobAsync(await bob.StartCreateVmAsync("work-vm"));
        Assert.Equal(JobState.Succeeded, retry.State);
    }

    /// <remarks>
    /// The VM exists on the hypervisor by the time the SSH wait times out, so rollback has to remove
    /// it — otherwise the host keeps an orphan VM and its disk chain while the name is handed back.
    /// </remarks>
    [Fact]
    public async Task An_unreachable_vm_fails_the_job_and_leaves_no_orphan_behind()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        app.Driver.Reachable = false;

        var job = await bob.WaitForJobAsync(await bob.StartCreateVmAsync("work-vm"));

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("did not answer ssh", job.Error);

        Assert.Contains("remove:work-vm", app.Driver.Calls);
        Assert.Equal(VmState.Absent, app.Driver.StateOf("work-vm"));
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/api/v1/vms/work-vm")).StatusCode);
        Assert.Empty(await app.Forwards.ListAsync("work-vm", CancellationToken.None));
        Assert.Empty(app.Forwards.Materialized);
        Assert.Contains("removing vm work-vm", string.Join("\n", job.Progress.Select(p => p.Text)));
    }

    [Fact]
    public async Task A_creation_that_fails_before_the_vm_exists_does_not_call_remove()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        app.IsoBuilder.Failure = new InvalidOperationException("wsl is not installed");

        var job = await bob.WaitForJobAsync(await bob.StartCreateVmAsync("work-vm"));

        Assert.Equal(JobState.Failed, job.State);
        Assert.DoesNotContain("remove:work-vm", app.Driver.Calls);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/api/v1/vms/work-vm")).StatusCode);
    }

    /// <remarks>
    /// While creation is still running the VM has no forward yet, and its internal address is not
    /// reachable from a client — so the endpoint route must say "not ready" rather than hand one out.
    /// </remarks>
    [Fact]
    public async Task The_endpoint_route_reports_not_ready_until_the_forward_exists()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        app.Driver.HoldCreate = true;

        var jobId = await bob.StartCreateVmAsync("work-vm");

        // The VM record exists (the name is reserved) but creation is still in flight.
        var vm = await (await bob.GetAsync("/api/v1/vms/work-vm")).ReadAsync<VmResponse>();
        Assert.Null(vm.SshForwardPort);

        using var tooEarly = await bob.GetAsync("/api/v1/vms/work-vm/endpoint");
        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);
        Assert.Contains("no ssh forward yet", await tooEarly.Content.ReadAsStringAsync());

        app.Driver.ReleaseCreate();
        await bob.WaitForJobAsync(jobId);

        var endpoint = await (await bob.GetAsync("/api/v1/vms/work-vm/endpoint")).ReadAsync<EndpointResponse>();
        Assert.Equal("buildbox.test", endpoint.SshHost);
        Assert.Equal(2201, endpoint.SshPort);
    }

    [Theory]
    [InlineData("Work-VM", 4, 8, 64, "name")]
    [InlineData("work vm", 4, 8, 64, "name")]
    [InlineData("work-vm", 0, 8, 64, "cpu")]
    [InlineData("work-vm", 4, 0, 64, "ramGb")]
    [InlineData("work-vm", 4, 8, 2, "diskGb")]
    [InlineData("work-vm", 4, 8, 100000, "diskGb")]
    public async Task Invalid_specs_are_rejected(string name, int cpu, int ramGb, int diskGb, string expected)
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");

        using var response = await bob.PostJsonAsync("/api/v1/vms", new { name, cpu, ramGb, diskGb });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_quota_is_enforced()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob", maxVms: 1);

        await bob.CreateVmAsync("work-vm");

        using var response = await bob.PostJsonAsync("/api/v1/vms",
            new { name = "second-vm", cpu = 2, ramGb = 4, diskGb = 32 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Quota reached", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Names_are_unique_across_users_on_one_host()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        using var alice = await app.CreateUserClientAsync("alice");
        await bob.CreateVmAsync("work-vm");

        using var response = await alice.PostJsonAsync("/api/v1/vms",
            new { name = "work-vm", cpu = 2, ramGb = 4, diskGb = 32 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_user_only_sees_their_own_vms_and_an_admin_sees_all()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        using var alice = await app.CreateUserClientAsync("alice");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);

        await bob.CreateVmAsync("bob-vm");
        await alice.CreateVmAsync("alice-vm");

        var bobs = await (await bob.GetAsync("/api/v1/vms")).ReadAsync<List<VmResponse>>();
        var alices = await (await alice.GetAsync("/api/v1/vms")).ReadAsync<List<VmResponse>>();
        var all = await (await admin.GetAsync("/api/v1/vms")).ReadAsync<List<VmResponse>>();

        Assert.Equal(new[] { "bob-vm" }, bobs.Select(v => v.Name).ToArray());
        Assert.Equal(new[] { "alice-vm" }, alices.Select(v => v.Name).ToArray());
        Assert.Equal(new[] { "alice-vm", "bob-vm" }, all.Select(v => v.Name).ToArray());
    }

    [Fact]
    public async Task The_wrong_owner_is_refused_on_every_per_vm_route()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        using var mallory = await app.CreateUserClientAsync("mallory");
        await bob.CreateVmAsync("work-vm");

        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.GetAsync("/api/v1/vms/work-vm")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.GetAsync("/api/v1/vms/work-vm/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.GetAsync("/api/v1/vms/work-vm/endpoint")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.GetAsync("/api/v1/vms/work-vm/forwards")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.GetAsync("/api/v1/vms/work-vm/idle-policy")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.DeleteAsync("/api/v1/vms/work-vm")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await mallory.PostJsonAsync("/api/v1/vms/work-vm/power", new { action = "stop" })).StatusCode);
    }

    [Fact]
    public async Task An_admin_may_act_on_someone_elses_vm()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await bob.CreateVmAsync("work-vm");

        var vm = await (await admin.GetAsync("/api/v1/vms/work-vm")).ReadAsync<VmResponse>();

        Assert.Equal("bob", vm.Owner);
    }

    [Fact]
    public async Task An_unknown_vm_is_a_404_rather_than_a_403()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/api/v1/vms/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.GetAsync("/api/v1/vms/NOPE")).StatusCode);
    }

    [Theory]
    [InlineData("stop", VmState.Off)]
    [InlineData("save", VmState.Saved)]
    [InlineData("start", VmState.Running)]
    public async Task Power_actions_reach_the_driver(string action, VmState expected)
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        await bob.CreateVmAsync("work-vm");

        var body = await (await bob.PostJsonAsync("/api/v1/vms/work-vm/power", new { action }))
            .ReadAsync<VmStateResponse>();

        Assert.Equal(expected, body.State);
        Assert.Equal(expected, app.Driver.StateOf("work-vm"));

        var state = await (await bob.GetAsync("/api/v1/vms/work-vm/state")).ReadAsync<VmStateResponse>();
        Assert.Equal(expected, state.State);
    }

    [Fact]
    public async Task An_unknown_power_action_is_rejected()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        await bob.CreateVmAsync("work-vm");

        using var response = await bob.PostJsonAsync("/api/v1/vms/work-vm/power", new { action = "explode" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Saving_is_refused_when_the_driver_cannot_suspend()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        await bob.CreateVmAsync("work-vm");
        app.Driver.Capabilities = app.Driver.Capabilities with { Suspend = false };

        using var response = await bob.PostJsonAsync("/api/v1/vms/work-vm/power", new { action = "save" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_vm_removes_it_and_frees_its_ports()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        await bob.CreateVmAsync("work-vm");
        await bob.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 3000, label = "vite", target = "host" });

        using var accepted = await bob.DeleteAsync("/api/v1/vms/work-vm");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var job = await bob.WaitForJobAsync((await accepted.ReadAsync<JobAcceptedResponse>()).JobId);

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/api/v1/vms/work-vm")).StatusCode);
        Assert.Empty(await app.Forwards.ListAsync("work-vm", CancellationToken.None));
        Assert.Empty(app.Forwards.Materialized);

        // The freed SSH port is handed out again to the next VM.
        var next = await bob.CreateVmAsync("next-vm");
        Assert.Equal(2201, next.ResultElement("endpoint").GetProperty("sshPort").GetInt32());
    }

    [Fact]
    public async Task Jobs_are_readable_by_their_submitter_and_by_admins_only()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        using var mallory = await app.CreateUserClientAsync("mallory");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);

        var job = await bob.CreateVmAsync("work-vm");

        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync($"/api/v1/jobs/{job.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/jobs/{job.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.GetAsync($"/api/v1/jobs/{job.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.GetAsync($"/api/v1/jobs/{job.Id}/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/api/v1/jobs/nope")).StatusCode);
    }

    [Fact]
    public async Task The_endpoint_route_reports_the_service_host_and_the_allocated_port()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        await bob.CreateVmAsync("work-vm");

        var endpoint = await (await bob.GetAsync("/api/v1/vms/work-vm/endpoint")).ReadAsync<EndpointResponse>();

        Assert.Equal("buildbox.test", endpoint.SshHost);
        Assert.Equal(2201, endpoint.SshPort);
    }

    [Fact]
    public async Task Refused_mutating_calls_are_audited_too()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        using var mallory = await app.CreateUserClientAsync("mallory");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await bob.CreateVmAsync("work-vm");

        await mallory.DeleteAsync("/api/v1/vms/work-vm");
        await mallory.PostJsonAsync("/api/v1/vms/work-vm/forwards", new { vmPort = 3000, label = "x" });

        var entries = await (await admin.GetAsync("/api/v1/audit")).ReadAsync<List<AuditResponse>>();

        Assert.Contains(entries, e => e is
        {
            Action: "vm.delete", Target: "work-vm", Actor: "mallory", Outcome: AuditOutcome.Denied,
        });
        Assert.Contains(entries, e => e is
        {
            Action: "forward.add", Target: "work-vm", Actor: "mallory", Outcome: AuditOutcome.Denied,
        });
    }

    [Fact]
    public async Task Vm_creation_is_audited()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await admin.CreateVmAsync("work-vm");

        var entries = await (await admin.GetAsync("/api/v1/audit")).ReadAsync<List<AuditResponse>>();

        Assert.Contains(entries, e => e is { Action: "vm.create", Target: "work-vm", Actor: "admin" });
        Assert.Contains(entries, e => e is { Action: "vm.create.completed", Target: "work-vm" });
    }
}
