using System.Net;
using Constructd.Api.Admin;
using Constructd.Api.Composition;
using Constructd.Api.Contracts;
using Constructd.Core.Configuration;
using Constructd.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Api;

/// <summary>
/// Per-VM public host names (plan §4.12): what the endpoint, the VM projection and the forwards
/// advertise, with and without a <c>Constructd:PublicHostPattern</c>. The pattern-less case is the
/// regression bar — every VM keeps being advertised on the one <c>PublicHost</c>, exactly as before.
/// </summary>
public class PublicHostTests
{
    private static readonly Dictionary<string, string?> WithPattern = new()
    {
        ["Constructd:PublicHostPattern"] = "{name}.vpn.example",
    };

    private static async Task<(TestApp App, HttpClient Owner, string VmToken)> SetupAsync(
        IDictionary<string, string?>? settings = null)
    {
        var app = new TestApp(settings);
        var owner = await app.CreateUserClientAsync("bob");
        var job = await owner.CreateVmAsync("work-vm");
        return (app, owner, job.VmToken());
    }

    [Fact]
    public async Task Without_a_pattern_the_endpoint_advertises_the_service_public_host()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;

        var endpoint = await (await owner.GetAsync("/api/v1/vms/work-vm/endpoint"))
            .ReadAsync<EndpointResponse>();

        Assert.Equal("buildbox.test", endpoint.SshHost);
        Assert.Equal("buildbox.test", endpoint.PublicHost);
        Assert.InRange(endpoint.SshPort, 2201, 2299);
    }

    [Fact]
    public async Task With_a_pattern_the_endpoint_advertises_the_vms_own_name()
    {
        var (app, owner, _) = await SetupAsync(WithPattern);
        using var _app = app;
        using var _owner = owner;

        var endpoint = await (await owner.GetAsync("/api/v1/vms/work-vm/endpoint"))
            .ReadAsync<EndpointResponse>();

        // SSH is still the service host plus the allocated forward: the pattern moves the WEB
        // name, never where a client dials.
        Assert.Equal("buildbox.test", endpoint.SshHost);
        Assert.Equal("work-vm.vpn.example", endpoint.PublicHost);
    }

    [Fact]
    public async Task The_creation_job_result_carries_the_public_host()
    {
        var app = new TestApp(WithPattern);
        using var _app = app;
        using var owner = await app.CreateUserClientAsync("bob");

        var job = await owner.CreateVmAsync("work-vm");
        var endpoint = job.ResultElement("endpoint");

        Assert.Equal("buildbox.test", endpoint.GetProperty("sshHost").GetString());
        Assert.Equal("work-vm.vpn.example", endpoint.GetProperty("publicHost").GetString());
    }

    [Fact]
    public async Task Two_vms_on_one_host_get_two_names()
    {
        var app = new TestApp(WithPattern);
        using var _app = app;
        using var owner = await app.CreateUserClientAsync("bob");

        await owner.CreateVmAsync("work-vm");
        await owner.CreateVmAsync("play-vm");

        var vms = await (await owner.GetAsync("/api/v1/vms")).ReadAsync<List<VmResponse>>();

        // The whole point of the setting: a browser tells the two web UIs apart by HOST, so two
        // VMs must never differ only by port.
        Assert.Equal("work-vm.vpn.example", vms.Single(v => v.Name == "work-vm").PublicHost);
        Assert.Equal("play-vm.vpn.example", vms.Single(v => v.Name == "play-vm").PublicHost);
    }

    [Fact]
    public async Task Without_a_pattern_two_vms_share_the_service_public_host()
    {
        var app = new TestApp();
        using var _app = app;
        using var owner = await app.CreateUserClientAsync("bob");

        await owner.CreateVmAsync("work-vm");
        await owner.CreateVmAsync("play-vm");

        var vms = await (await owner.GetAsync("/api/v1/vms")).ReadAsync<List<VmResponse>>();

        Assert.All(vms, vm => Assert.Equal("buildbox.test", vm.PublicHost));
    }

    [Fact]
    public async Task A_host_forward_is_advertised_under_the_vms_own_name()
    {
        var (app, owner, _) = await SetupAsync(WithPattern);
        using var _app = app;
        using var _owner = owner;

        var forward = await (await owner.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 5178, label = "t3", target = "host" })).ReadAsync<ForwardResponse>();

        Assert.NotNull(forward.PublicPort);
        Assert.Equal($"http://work-vm.vpn.example:{forward.PublicPort}/", forward.Url);
    }

    [Fact]
    public async Task Without_a_pattern_a_host_forward_is_advertised_on_the_service_host()
    {
        var (app, owner, _) = await SetupAsync();
        using var _app = app;
        using var _owner = owner;

        var forward = await (await owner.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 5178, label = "t3", target = "host" })).ReadAsync<ForwardResponse>();

        Assert.Equal($"http://buildbox.test:{forward.PublicPort}/", forward.Url);
    }

    [Fact]
    public async Task The_forward_list_and_the_vm_projection_agree_with_the_endpoint()
    {
        var (app, owner, vmToken) = await SetupAsync(WithPattern);
        using var _app = app;
        using var _owner = owner;

        await owner.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 4096, label = "opencode", target = "host" });

        // The guest reads the same list with its own scoped token — `construct expose` builds the
        // link it prints from exactly this url.
        using var guest = app.CreateVmTokenClient(vmToken);
        var listed = await (await guest.GetAsync("/api/v1/vms/work-vm/forwards"))
            .ReadAsync<List<ForwardResponse>>();
        var vm = await (await owner.GetAsync("/api/v1/vms/work-vm")).ReadAsync<VmResponse>();
        var endpoint = await (await owner.GetAsync("/api/v1/vms/work-vm/endpoint"))
            .ReadAsync<EndpointResponse>();

        var forward = Assert.Single(listed);
        Assert.StartsWith("http://work-vm.vpn.example:", forward.Url);
        Assert.Equal("work-vm.vpn.example", vm.PublicHost);
        Assert.Equal("work-vm.vpn.example", endpoint.PublicHost);
        Assert.Equal(forward.Url, vm.Forwards.Single().Url);
    }

    [Fact]
    public async Task A_client_forwards_ack_link_is_the_users_pc_not_the_vms_public_host()
    {
        var (app, owner, _) = await SetupAsync(WithPattern);
        using var _app = app;
        using var _owner = owner;

        var forward = await (await owner.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 3000, label = "vite", target = "client" })).ReadAsync<ForwardResponse>();

        var acked = await (await owner.PostJsonAsync(
            $"/api/v1/vms/work-vm/forwards/{forward.Id}/ack",
            new { status = "open", localPort = 18800, hostLabel = "alice-pc" }))
            .ReadAsync<ForwardResponse>();

        // A client forward lives on the user's PC; the VM's public host has nothing to do with it.
        Assert.Equal("http://alice-pc:18800/", acked.Url);
    }

    [Fact]
    public void An_unusable_pattern_stops_the_host_from_starting()
    {
        // Composition — not a request — is where this fails, and it fails in EVERY mode: a pattern
        // that cannot render a host name would otherwise surface as a broken URL weeks later.
        var services = new ServiceCollection();
        var options = new ConstructdOptions { Fake = true, PublicHostPattern = "vpn.example" };

        var error = Assert.Throws<InvalidOperationException>(
            () => services.AddConstructdServices(options));

        Assert.Contains("{name}", error.Message);
    }

    [Fact]
    public async Task Admin_host_status_prints_the_pattern_and_an_example()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var services = new ServiceCollection()
            .AddSingleton(new ConstructdOptions
            {
                PublicHost = "buildbox.test",
                PublicHostPattern = "{name}.vpn.example",
            })
            .BuildServiceProvider();

        var exit = await AdminCli.RunAsync(
            ["host", "status"], services, output, error, CancellationToken.None);

        Assert.Equal(AdminExitCode.Ok, exit);
        var text = output.ToString();
        Assert.Contains("{name}.vpn.example", text);
        Assert.Contains("work-vm.vpn.example", text);
        // The DNS record an admin has to create for the pattern to resolve at all.
        Assert.Contains("*.vpn.example", text);
    }

    [Fact]
    public async Task Admin_host_status_says_when_there_is_no_pattern()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var services = new ServiceCollection()
            .AddSingleton(new ConstructdOptions { PublicHost = "buildbox.test" })
            .BuildServiceProvider();

        var exit = await AdminCli.RunAsync(
            ["host", "status"], services, output, error, CancellationToken.None);

        Assert.Equal(AdminExitCode.Ok, exit);
        Assert.Contains("unset", output.ToString());
    }
}
