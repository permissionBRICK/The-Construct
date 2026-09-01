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
}
