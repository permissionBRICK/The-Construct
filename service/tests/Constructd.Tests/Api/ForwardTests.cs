using System.Net;
using Constructd.Api.Contracts;
using Constructd.Core.Domain;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

public class ForwardTests
{
    private static async Task<(TestApp App, HttpClient Owner, string VmToken)> SetupAsync(
        bool allowHostForwards = true,
        IDictionary<string, string?>? settings = null)
    {
        var app = new TestApp(settings);
        var owner = await app.CreateUserClientAsync("bob", allowHostForwards: allowHostForwards);
        var job = await owner.CreateVmAsync("work-vm");
        return (app, owner, job.VmToken());
    }

    [Fact]
    public async Task A_client_forward_is_recorded_but_not_materialized_on_the_host()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;

        var forward = await (await owner.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 3000, label = "vite dev", target = "client" })).ReadAsync<ForwardResponse>();

        Assert.Equal(ForwardTarget.Client, forward.Target);
        Assert.Null(forward.PublicPort);
        Assert.Null(forward.Url);
        Assert.Equal(3000, forward.VmPort);
        Assert.Equal("vite dev", forward.Label);
        Assert.DoesNotContain(app.Forwards.Materialized, kv => kv.Key == forward.Id);
    }

    [Fact]
    public async Task Client_is_the_default_target()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;

        var forward = await (await owner.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 3000, label = "vite" })).ReadAsync<ForwardResponse>();

        Assert.Equal(ForwardTarget.Client, forward.Target);
    }

    [Fact]
    public async Task A_host_forward_gets_a_public_port_from_the_app_range_and_a_url()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;

        var forward = await (await owner.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 8080, label = "webhook", target = "host" })).ReadAsync<ForwardResponse>();

        Assert.Equal(ForwardTarget.Host, forward.Target);
        Assert.NotNull(forward.PublicPort);
        Assert.InRange(forward.PublicPort!.Value, 2300, 2999);
        Assert.Equal($"http://buildbox.test:{forward.PublicPort}/", forward.Url);
        Assert.Contains(app.Forwards.Materialized, kv => kv.Key == forward.Id);
    }

    [Fact]
    public async Task Host_forwards_can_be_disabled_per_user_by_an_admin()
    {
        var (app, owner, _) = await SetupAsync(allowHostForwards: false);
        using var _app = app;
        using var _owner = owner;

        using var refused = await owner.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 8080, label = "webhook", target = "host" });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("target=client", await refused.Content.ReadAsStringAsync());

        // Client forwards keep working — they never touch the LAN.
        using var allowed = await owner.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 8080, label = "webhook", target = "client" });
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    public async Task An_admin_cannot_route_around_the_owners_host_forward_restriction()
    {
        var (app, owner, _) = await SetupAsync(allowHostForwards: false);
        using var _app = app;
        using var _owner = owner;
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);

        using var response = await admin.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 8080, label = "webhook", target = "host" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_vm_token_manages_its_own_forwards_only()
    {
        var (app, owner, vmToken) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        await owner.CreateVmAsync("other-vm");
        using var guest = app.CreateVmTokenClient(vmToken);

        // Its own VM: allowed.
        var created = await guest.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 3000, label = "vite", target = "client" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var forward = await created.ReadAsync<ForwardResponse>();

        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync("/api/v1/vms/work-vm/forwards")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await guest.DeleteAsync($"/api/v1/vms/work-vm/forwards/{forward.Id}")).StatusCode);

        // Another VM: refused, even though it belongs to the same user.
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync("/api/v1/vms/other-vm/forwards")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostJsonAsync("/api/v1/vms/other-vm/forwards",
            new { vmPort = 3000, label = "x", target = "client" })).StatusCode);
    }

    [Fact]
    public async Task Another_users_forwards_are_out_of_reach()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        using var mallory = await app.CreateUserClientAsync("mallory");

        using var response = await mallory.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 3000, label = "x", target = "client" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Forwards_are_listed_on_the_vm_and_removable()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;

        var forward = await (await owner.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 3000, label = "vite", target = "host" })).ReadAsync<ForwardResponse>();

        var vm = await (await owner.GetAsync("/api/v1/vms/work-vm")).ReadAsync<VmResponse>();
        Assert.Equal(forward.Id, Assert.Single(vm.Forwards).Id);

        var listed = await (await owner.GetAsync("/api/v1/vms/work-vm/forwards")).ReadAsync<List<ForwardResponse>>();
        Assert.Single(listed);

        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.DeleteAsync($"/api/v1/vms/work-vm/forwards/{forward.Id}")).StatusCode);
        Assert.Empty(await (await owner.GetAsync("/api/v1/vms/work-vm/forwards")).ReadAsync<List<ForwardResponse>>());
        Assert.DoesNotContain(app.Forwards.Materialized, kv => kv.Key == forward.Id);
    }

    [Fact]
    public async Task Deleting_an_unknown_forward_is_a_404()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;

        using var response = await owner.DeleteAsync("/api/v1/vms/work-vm/forwards/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_forward_of_one_vm_cannot_be_deleted_through_another()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        await owner.CreateVmAsync("other-vm");

        var forward = await (await owner.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 3000, label = "vite", target = "client" })).ReadAsync<ForwardResponse>();

        using var response = await owner.DeleteAsync($"/api/v1/vms/other-vm/forwards/{forward.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_per_vm_forward_count_is_capped()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;

        for (var i = 0; i < 3; i++)
        {
            using var ok = await owner.PostJsonAsync("/api/v1/vms/work-vm/forwards",
                new { vmPort = 3000 + i, label = $"p{i}", target = "client" });
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        using var refused = await owner.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 3100, label = "too many", target = "client" });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Theory]
    [InlineData(0, "client", "vmPort")]
    [InlineData(70000, "client", "vmPort")]
    [InlineData(3000, "elsewhere", "target")]
    public async Task Invalid_forward_payloads_are_rejected(int vmPort, string target, string expected)
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;

        using var response = await owner.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort, label = "x", target });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Exhausting_the_app_port_range_is_reported_as_a_conflict()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Constructd:AppForwardPorts:Start"] = "2300",
            ["Constructd:AppForwardPorts:End"] = "2300",
            ["Constructd:MaxForwardsPerVm"] = "5",
        };

        var (app, owner, _) = await SetupAsync(settings: settings);
        using var _app = app;
        using var _owner = owner;

        using var first = await owner.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 3000, label = "a", target = "host" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var second = await owner.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 3001, label = "b", target = "host" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Forward_changes_are_audited()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);

        var forward = await (await owner.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 3000, label = "vite", target = "host" })).ReadAsync<ForwardResponse>();
        await owner.DeleteAsync($"/api/v1/vms/work-vm/forwards/{forward.Id}");

        var entries = await (await admin.GetAsync("/api/v1/audit")).ReadAsync<List<AuditResponse>>();

        Assert.Contains(entries, e => e is { Action: "forward.add", Target: "work-vm", Actor: "bob" });
        Assert.Contains(entries, e => e is { Action: "forward.remove", Target: "work-vm", Actor: "bob" });
    }

    // ── The client-forward ack relay (plan §4.6) ────────────────────────────────────────────
    //
    // The extension opens the port on the user's PC and reports back here; `construct expose`,
    // polling the list with the VM token, turns that into the one link it prints. The shape tests
    // below mirror the CLI's own lenient parser — see ExposeCliContractTests.

    private static async Task<ForwardResponse> AddClientForwardAsync(HttpClient client, int vmPort = 5173) =>
        await (await client.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort, label = "vite dev", target = "client" })).ReadAsync<ForwardResponse>();

    [Fact]
    public async Task An_ack_turns_a_client_forward_into_a_link()
    {
        var (app, owner, vmToken) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        var forward = await AddClientForwardAsync(owner);

        // Before the ack the guest sees no link at all: "not open yet", keep waiting.
        using var guest = app.CreateVmTokenClient(vmToken);
        var queued = Assert.Single(
            await (await guest.GetAsync("/api/v1/vms/work-vm/forwards")).ReadAsync<List<ForwardResponse>>());
        Assert.Null(queued.Url);
        Assert.Null(queued.Status);
        Assert.Null(queued.LocalPort);

        var acked = await (await owner.PostJsonAsync($"/api/v1/vms/work-vm/forwards/{forward.Id}/ack",
            new { status = "open", localPort = 5173 })).ReadAsync<ForwardResponse>();

        Assert.Equal("open", acked.Status);
        Assert.Equal(5173, acked.LocalPort);
        Assert.Null(acked.HostLabel);
        Assert.Equal("http://localhost:5173/", acked.Url);

        // ...and the guest, with its own token, sees exactly the same thing.
        var listed = Assert.Single(
            await (await guest.GetAsync("/api/v1/vms/work-vm/forwards")).ReadAsync<List<ForwardResponse>>());
        Assert.Equal("http://localhost:5173/", listed.Url);
        Assert.Equal(5173, listed.LocalPort);
    }

    [Fact]
    public async Task A_host_label_names_the_users_pc_in_the_link()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        var forward = await AddClientForwardAsync(owner);

        var acked = await (await owner.PostJsonAsync($"/api/v1/vms/work-vm/forwards/{forward.Id}/ack",
            new { status = "open", localPort = 15173, hostLabel = "christoph-pc" })).ReadAsync<ForwardResponse>();

        Assert.Equal("christoph-pc", acked.HostLabel);
        Assert.Equal("http://christoph-pc:15173/", acked.Url);
    }

    [Fact]
    public async Task An_error_ack_carries_the_reason_and_no_link()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        var forward = await AddClientForwardAsync(owner);

        var acked = await (await owner.PostJsonAsync($"/api/v1/vms/work-vm/forwards/{forward.Id}/ack",
            new { status = "error", message = "no free local port" })).ReadAsync<ForwardResponse>();

        Assert.Equal("error", acked.Status);
        Assert.Equal("no free local port", acked.Message);
        // A url would be printed INSTEAD of the failure by the CLI, which checks url first.
        Assert.Null(acked.Url);
    }

    [Fact]
    public async Task A_re_ack_replaces_the_previous_one()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        var forward = await AddClientForwardAsync(owner);

        await owner.PostJsonAsync($"/api/v1/vms/work-vm/forwards/{forward.Id}/ack",
            new { status = "error", message = "port busy" });
        var reacked = await (await owner.PostJsonAsync($"/api/v1/vms/work-vm/forwards/{forward.Id}/ack",
            new { status = "open", localPort = 18800 })).ReadAsync<ForwardResponse>();

        Assert.Equal("open", reacked.Status);
        Assert.Equal("http://localhost:18800/", reacked.Url);
        Assert.Equal(string.Empty, reacked.Message);
    }

    /// <remarks>
    /// The whole reason this route is not <c>vm-scoped</c>: the guest ASKS for a forward, and a VM
    /// that could also answer could hand its own agents a link to a port nothing is listening on.
    /// </remarks>
    [Fact]
    public async Task The_vms_own_token_cannot_fake_a_client_ack()
    {
        var (app, owner, vmToken) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        var forward = await AddClientForwardAsync(owner);
        using var guest = app.CreateVmTokenClient(vmToken);

        using var response = await guest.PostJsonAsync($"/api/v1/vms/work-vm/forwards/{forward.Id}/ack",
            new { status = "open", localPort = 5173 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // ...and nothing was recorded.
        var listed = Assert.Single(
            await (await owner.GetAsync("/api/v1/vms/work-vm/forwards")).ReadAsync<List<ForwardResponse>>());
        Assert.Null(listed.Status);
        Assert.Null(listed.Url);
    }

    [Fact]
    public async Task An_admin_may_ack_and_another_user_may_not()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        using var mallory = await app.CreateUserClientAsync("mallory");
        var forward = await AddClientForwardAsync(owner);

        using var refused = await mallory.PostJsonAsync($"/api/v1/vms/work-vm/forwards/{forward.Id}/ack",
            new { status = "open", localPort = 5173 });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using var allowed = await admin.PostJsonAsync($"/api/v1/vms/work-vm/forwards/{forward.Id}/ack",
            new { status = "open", localPort = 5173 });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task An_anonymous_ack_is_unauthorized()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        var forward = await AddClientForwardAsync(owner);
        using var anonymous = app.CreateAnonymousClient();

        using var response = await anonymous.PostJsonAsync($"/api/v1/vms/work-vm/forwards/{forward.Id}/ack",
            new { status = "open", localPort = 5173 });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Acking_an_unknown_or_foreign_forward_is_a_404()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        await owner.CreateVmAsync("other-vm");
        var forward = await AddClientForwardAsync(owner);

        using var unknown = await owner.PostJsonAsync("/api/v1/vms/work-vm/forwards/does-not-exist/ack",
            new { status = "open", localPort = 5173 });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // The id exists — but not on the VM it is being acked through.
        using var foreign = await owner.PostJsonAsync($"/api/v1/vms/other-vm/forwards/{forward.Id}/ack",
            new { status = "open", localPort = 5173 });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Acking_a_host_forward_is_a_conflict()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;

        var forward = await (await owner.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 8080, label = "webhook", target = "host" })).ReadAsync<ForwardResponse>();

        using var response = await owner.PostJsonAsync($"/api/v1/vms/work-vm/forwards/{forward.Id}/ack",
            new { status = "open", localPort = 8080 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("target=host", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("open", null, "localPort")]
    [InlineData("open", 0, "localPort")]
    [InlineData("open", 70000, "localPort")]
    [InlineData("error", 70000, "localPort")]
    [InlineData("sideways", 5173, "status")]
    [InlineData(null, 5173, "status")]
    public async Task Invalid_ack_payloads_are_rejected(string? status, int? localPort, string expected)
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        var forward = await AddClientForwardAsync(owner);

        using var response = await owner.PostJsonAsync($"/api/v1/vms/work-vm/forwards/{forward.Id}/ack",
            new { status, localPort });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// Both strings are echoed to a guest CLI that prints them; a newline in either would let one
    /// field forge another line of `construct expose --list`'s output.
    /// </remarks>
    [Fact]
    public async Task A_host_label_and_a_message_cannot_carry_control_characters()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        var forward = await AddClientForwardAsync(owner);

        var acked = await (await owner.PostJsonAsync($"/api/v1/vms/work-vm/forwards/{forward.Id}/ack",
            new
            {
                status = "open",
                localPort = 5173,
                hostLabel = "pc\r\nX-Injected: 1",
                message = "line one\u0000\r\nline two",
            })).ReadAsync<ForwardResponse>();

        // The control characters go first (Sanitize), and what is left — "pcX-Injected: 1" — is not
        // a host label at all, so ForwardHost.Normalize drops it and the link falls back to
        // loopback. Same answer the extension's own sanitizeHostLabel gives that string.
        Assert.Null(acked.HostLabel);
        Assert.Equal("http://localhost:5173/", acked.Url);
        Assert.Equal("line oneline two", acked.Message);
        Assert.DoesNotContain("\n", acked.Url!);
    }

    [Fact]
    public async Task An_ack_is_audited_without_its_forward_being_guessable_from_the_target()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var forward = await AddClientForwardAsync(owner);

        await owner.PostJsonAsync($"/api/v1/vms/work-vm/forwards/{forward.Id}/ack",
            new { status = "open", localPort = 5173, hostLabel = "christoph-pc" });

        var entries = await (await admin.GetAsync("/api/v1/audit?limit=1000")).ReadAsync<List<AuditResponse>>();
        var entry = Assert.Single(entries, e => e.Action == "forward.ack");

        Assert.Equal("work-vm", entry.Target);
        Assert.Equal("bob", entry.Actor);
        Assert.Equal(AuditOutcome.Success, entry.Outcome);
        Assert.Contains($"id={forward.Id}", entry.Detail);
        Assert.Contains("status=open", entry.Detail);
        Assert.Contains("localPort=5173", entry.Detail);
    }

    [Fact]
    public async Task A_refused_ack_is_audited_too()
    {
        var (app, owner, vmToken) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var forward = await AddClientForwardAsync(owner);
        using var guest = app.CreateVmTokenClient(vmToken);

        await guest.PostJsonAsync($"/api/v1/vms/work-vm/forwards/{forward.Id}/ack",
            new { status = "open", localPort = 5173 });

        var entries = await (await admin.GetAsync("/api/v1/audit?limit=1000")).ReadAsync<List<AuditResponse>>();

        Assert.Contains(entries, e => e is { Action: "forward.ack", Outcome: AuditOutcome.Denied });
    }

    [Fact]
    public async Task An_ack_of_a_vm_being_deleted_is_a_conflict()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        var forward = await AddClientForwardAsync(owner);

        // Hold the removal job so the VM stays fenced while the ack is attempted.
        app.Forwards.HoldRemoveAll = true;
        using var accepted = await owner.DeleteAsync("/api/v1/vms/work-vm");
        var jobId = (await accepted.ReadAsync<JobAcceptedResponse>()).JobId;
        await app.Forwards.RemoveAllStarted.Task;

        using var response = await owner.PostJsonAsync($"/api/v1/vms/work-vm/forwards/{forward.Id}/ack",
            new { status = "open", localPort = 5173 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("being deleted", await response.Content.ReadAsStringAsync());

        app.Forwards.ReleaseRemoveAll();
        Assert.Equal(JobState.Succeeded, (await owner.WaitForJobAsync(jobId)).State);
    }

    [Fact]
    public async Task The_vm_projection_carries_the_ack_too()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;
        var forward = await AddClientForwardAsync(owner);

        await owner.PostJsonAsync($"/api/v1/vms/work-vm/forwards/{forward.Id}/ack",
            new { status = "open", localPort = 5173, hostLabel = "christoph-pc" });

        var vm = await (await owner.GetAsync("/api/v1/vms/work-vm")).ReadAsync<VmResponse>();

        Assert.Equal("http://christoph-pc:5173/", Assert.Single(vm.Forwards).Url);
    }
}
