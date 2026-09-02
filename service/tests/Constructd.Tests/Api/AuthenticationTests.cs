using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Core.Domain;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

public class AuthenticationTests
{
    [Theory]
    [InlineData("GET", "/api/v1/whoami")]
    [InlineData("GET", "/api/v1/vms")]
    [InlineData("POST", "/api/v1/vms")]
    [InlineData("GET", "/api/v1/vms/work-vm")]
    [InlineData("DELETE", "/api/v1/vms/work-vm")]
    [InlineData("POST", "/api/v1/vms/work-vm/power")]
    [InlineData("GET", "/api/v1/vms/work-vm/state")]
    [InlineData("GET", "/api/v1/vms/work-vm/endpoint")]
    [InlineData("GET", "/api/v1/vms/work-vm/forwards")]
    [InlineData("POST", "/api/v1/vms/work-vm/forwards")]
    [InlineData("DELETE", "/api/v1/vms/work-vm/forwards/abc")]
    [InlineData("GET", "/api/v1/vms/work-vm/idle-policy")]
    [InlineData("PUT", "/api/v1/vms/work-vm/idle-policy")]
    [InlineData("POST", "/api/v1/vms/work-vm/activity")]
    [InlineData("GET", "/api/v1/jobs/abc")]
    [InlineData("GET", "/api/v1/jobs/abc/events")]
    [InlineData("POST", "/api/v1/users")]
    [InlineData("DELETE", "/api/v1/users/bob")]
    [InlineData("POST", "/api/v1/users/bob/tokens")]
    [InlineData("GET", "/api/v1/audit")]
    public async Task Every_route_answers_401_without_a_credential(string method, string url)
    {
        using var app = new TestApp();
        using var client = app.CreateAnonymousClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_token_is_rejected()
    {
        using var app = new TestApp();
        using var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");

        using var response = await client.GetAsync("/api/v1/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_of_a_deleted_user_stops_working()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        using var bob = await app.CreateUserClientAsync("bob");

        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/api/v1/whoami")).StatusCode);

        using var deleted = await admin.DeleteAsync("/api/v1/users/bob");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var response = await bob.GetAsync("/api/v1/whoami");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_authenticated_but_unenrolled_identity_can_see_that_it_is_not_enrolled()
    {
        using var app = new TestApp();
        using var client = app.CreateTestIdentityClient("DOMAIN\\stranger");

        using var whoami = await client.GetAsync("/api/v1/whoami");
        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);

        var body = await whoami.ReadAsync<WhoAmIResponse>();
        Assert.False(body.Known);
        Assert.Null(body.Role);
        Assert.Equal("DOMAIN\\stranger", body.Name);

        // ...but reaches nothing else.
        using var vms = await client.GetAsync("/api/v1/vms");
        Assert.Equal(HttpStatusCode.Forbidden, vms.StatusCode);
    }

    [Fact]
    public async Task The_negotiate_stand_in_scheme_resolves_a_role_from_the_user_store()
    {
        using var app = new TestApp();
        await app.AddUserAsync("DOMAIN\\christoph", Role.Admin, maxVms: 3);
        using var client = app.CreateTestIdentityClient("DOMAIN\\christoph");

        var body = await (await client.GetAsync("/api/v1/whoami")).ReadAsync<WhoAmIResponse>();

        Assert.True(body.Known);
        Assert.Equal(Role.Admin, body.Role);
        Assert.Equal(3, body.MaxVms);
        Assert.Equal(ConstructdSchemes.TestIdentity, body.Scheme);
    }

    [Fact]
    public async Task Identity_matching_is_case_insensitive_like_windows_names()
    {
        using var app = new TestApp();
        await app.AddUserAsync("DOMAIN\\christoph");
        using var client = app.CreateTestIdentityClient("domain\\CHRISTOPH");

        var body = await (await client.GetAsync("/api/v1/whoami")).ReadAsync<WhoAmIResponse>();

        Assert.True(body.Known);
    }

    [Fact]
    public async Task A_token_client_reports_its_role_and_quota()
    {
        using var app = new TestApp();
        using var client = await app.CreateUserClientAsync("bob", Role.User, maxVms: 2);

        var body = await (await client.GetAsync("/api/v1/whoami")).ReadAsync<WhoAmIResponse>();

        Assert.Equal("user", body.Kind);
        Assert.Equal(Role.User, body.Role);
        Assert.Equal(2, body.MaxVms);
        Assert.True(body.AllowHostForwards);
        Assert.Equal(ConstructdSchemes.Bearer, body.Scheme);
    }

    /// <summary>
    /// A VM-scoped token is valid for exactly four calls — its own VM's forwards (list, add, remove)
    /// and its own heartbeat. Every other route in the API, including <c>/whoami</c>, must refuse it.
    /// </summary>
    [Fact]
    public async Task A_vm_token_reaches_only_its_own_forwards_and_heartbeat()
    {
        using var app = new TestApp();
        using var owner = await app.CreateUserClientAsync("bob");
        var job = await owner.CreateVmAsync("work-vm");
        using var guest = app.CreateVmTokenClient(job.VmToken());

        (string Method, string Url)[] allowed =
        [
            ("GET", "/api/v1/vms/work-vm/forwards"),
            ("POST", "/api/v1/vms/work-vm/forwards"),
            ("DELETE", "/api/v1/vms/work-vm/forwards/nope"),
            ("POST", "/api/v1/vms/work-vm/activity"),
        ];

        (string Method, string Url)[] refused =
        [
            ("GET", "/api/v1/whoami"),
            ("GET", "/api/v1/vms"),
            ("POST", "/api/v1/vms"),
            ("GET", "/api/v1/vms/work-vm"),
            ("DELETE", "/api/v1/vms/work-vm"),
            ("POST", "/api/v1/vms/work-vm/power"),
            ("GET", "/api/v1/vms/work-vm/state"),
            ("GET", "/api/v1/vms/work-vm/endpoint"),
            ("GET", "/api/v1/vms/work-vm/idle-policy"),
            ("PUT", "/api/v1/vms/work-vm/idle-policy"),
            ("GET", $"/api/v1/jobs/{job.Id}"),
            ("GET", $"/api/v1/jobs/{job.Id}/events"),
            ("POST", "/api/v1/users"),
            ("DELETE", "/api/v1/users/bob"),
            ("POST", "/api/v1/users/bob/tokens"),
            ("GET", "/api/v1/audit"),
        ];

        foreach (var (method, url) in refused)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            using var response = await guest.SendAsync(request);
            Assert.True(
                response.StatusCode == HttpStatusCode.Forbidden,
                $"{method} {url} answered {(int)response.StatusCode}, expected 403 for a VM token");
        }

        foreach (var (method, url) in allowed)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url)
            {
                Content = JsonContent.Create(new { vmPort = 3000, label = "vite", busy = true }),
            };
            using var response = await guest.SendAsync(request);
            Assert.True(
                response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized),
                $"{method} {url} answered {(int)response.StatusCode}, expected the VM's own token to pass");
        }
    }

    [Fact]
    public async Task A_vm_token_cannot_reach_another_vm_of_the_same_owner()
    {
        using var app = new TestApp();
        using var owner = await app.CreateUserClientAsync("bob");
        var job = await owner.CreateVmAsync("work-vm");
        await owner.CreateVmAsync("other-vm");
        using var guest = app.CreateVmTokenClient(job.VmToken());

        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync("/api/v1/vms/other-vm/forwards")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await guest.PostJsonAsync("/api/v1/vms/other-vm/activity", new { busy = true })).StatusCode);
    }

    [Fact]
    public async Task A_revoked_vm_token_stops_working_when_a_new_one_is_issued()
    {
        using var app = new TestApp();
        using var owner = await app.CreateUserClientAsync("bob");
        var job = await owner.CreateVmAsync("work-vm");
        var firstToken = job.VmToken();

        var secondToken = await app.Service<Constructd.Core.Abstractions.ITokenService>()
            .IssueVmTokenAsync("work-vm", CancellationToken.None);

        using var stale = app.CreateVmTokenClient(firstToken);
        using var fresh = app.CreateVmTokenClient(secondToken);

        // /whoami is closed to VM tokens; the forwards route is the one they may call.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await stale.GetAsync("/api/v1/vms/work-vm/forwards")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await fresh.GetAsync("/api/v1/vms/work-vm/forwards")).StatusCode);
    }

    [Fact]
    public void The_host_refuses_to_start_without_a_hypervisor_platform()
    {
        // The test-identity scheme exists only in fake mode, and with fakes off the host needs the
        // real platform implementations — which are Windows-only (Hyper-V, WSL, netsh). On this
        // machine that means it must refuse to start rather than come up half-wired.
        var settings = new Dictionary<string, string?>
        {
            ["Constructd:Fake"] = "false",
            ["Constructd:Persistence"] = "Memory",
        };
        using var app = new TestApp(settings);

        var ex = Assert.ThrowsAny<Exception>(() => app.CreateAnonymousClient());
        var message = Flatten(ex);

        Assert.Contains("no hypervisor platform here", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--fake", message, StringComparison.OrdinalIgnoreCase);
    }

    private static string Flatten(Exception ex) =>
        ex.InnerException is null ? ex.Message : $"{ex.Message} {Flatten(ex.InnerException)}";
}
