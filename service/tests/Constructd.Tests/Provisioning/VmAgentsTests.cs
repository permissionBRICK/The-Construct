using System.Net;
using Constructd.Api.Contracts;
using Constructd.Api.Jobs;
using Constructd.Fakes;
using Constructd.Tests.Support;

namespace Constructd.Tests.Provisioning;

/// <summary>The coding agents of a VM: reported with the guest's usage report, read with <c>GET /vms/{name}/agents</c>.</summary>
public sealed class VmAgentsTests
{
    private const string Url = $"/api/v1/vms/{ProvisionHarness.Vm}/agents";

    private static object Report(object agents) => new { generatedAt = DateTimeOffset.UtcNow, days = Array.Empty<object>(), agents };

    [Fact]
    public async Task The_guest_reports_its_agents_and_the_owner_sees_whether_a_newer_one_is_out()
    {
        await using var h = await ProvisionHarness.StartAsync();
        var empty = await (await h.Client.GetAsync(Url)).ReadAsync<VmAgentsResponse>();
        Assert.Null(empty.ReportedAt);
        Assert.Empty(empty.Agents);

        var latest = h.App.Service<FakeAgentReleases>().Latest;
        latest["claude"] = "2.2.0";
        latest["codex"] = "0.98.0";
        latest["t3@nightly"] = "0.0.30-nightly.1";
        await h.Client.PutJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/desired",
            new { projects = Array.Empty<string>(), settings = new { t3CodeChannel = "nightly" } });
        using var vm = h.App.CreateVmTokenClient(h.VmToken!);
        var posted = await vm.PostJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/usage", Report(new object[]
        {
            new { tool = "claude", version = "2.1.4" }, new { tool = "codex", version = "0.98.0" }, new { tool = "t3", version = "0.0.29-nightly.7" },
        }));
        Assert.Equal(HttpStatusCode.NoContent, posted.StatusCode);

        var read = await (await h.Client.GetAsync(Url)).ReadAsync<VmAgentsResponse>();
        Assert.NotNull(read.ReportedAt);
        Assert.Equal([
            new VmAgentResponse("claude", "Claude Code", "2.1.4", "2.2.0", true),
            new VmAgentResponse("codex", "Codex", "0.98.0", "0.98.0", false),
            new VmAgentResponse("t3", "T3 Code", "0.0.29-nightly.7", "0.0.30-nightly.1", true),
        ], read.Agents);

        // A usage report without the field leaves the agents as they were; another user reads nothing.
        Assert.Equal(HttpStatusCode.NoContent, (await vm.PostJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/usage",
            new { generatedAt = DateTimeOffset.UtcNow, days = Array.Empty<object>() })).StatusCode);
        Assert.Equal(3, (await (await h.Client.GetAsync(Url)).ReadAsync<VmAgentsResponse>()).Agents.Count);
        var bob = await h.App.CreateUserClientAsync("bob");
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync(Url)).StatusCode);
    }

    [Fact]
    public async Task An_opencode_1_vm_is_offered_opencode_2_whose_version_line_names_the_tool()
    {
        await using var h = await ProvisionHarness.StartAsync();
        h.App.Service<FakeAgentReleases>().Latest["opencode"] = "2.3.2";
        using var vm = h.App.CreateVmTokenClient(h.VmToken!);
        Assert.Equal(HttpStatusCode.NoContent, (await vm.PostJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/usage",
            Report(new[] { new { tool = "opencode", version = "1.19.6" } }))).StatusCode);
        Assert.Equal([new VmAgentResponse("opencode", "OpenCode", "1.19.6", "2.3.2", true)],
            (await (await h.Client.GetAsync(Url)).ReadAsync<VmAgentsResponse>()).Agents);

        // The guest reports the first version in `opencode --version`, which OpenCode 2 prints as "opencode v2.3.2".
        Assert.Equal(HttpStatusCode.NoContent, (await vm.PostJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/usage",
            Report(new[] { new { tool = "opencode", version = "2.3.2" } }))).StatusCode);
        Assert.Equal([new VmAgentResponse("opencode", "OpenCode", "2.3.2", "2.3.2", false)],
            (await (await h.Client.GetAsync(Url)).ReadAsync<VmAgentsResponse>()).Agents);
    }

    [Fact]
    public async Task The_latest_opencode_is_read_from_npm_opencode_cli()
    {
        var asked = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            asked.Add(request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"name":"@opencode/cli","version":"2.3.2","_nodeVersion":"24.1.0"}"""),
            };
        }));
        var releases = new AgentReleases(http, new MutableClock());
        Assert.Equal("2.3.2", await releases.LatestAsync("opencode", null, default));
        Assert.Equal(["https://registry.npmjs.org/@opencode/cli/latest"], asked);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    [Fact]
    public async Task Unknown_tools_and_odd_versions_are_refused()
    {
        await using var h = await ProvisionHarness.StartAsync();
        using var vm = h.App.CreateVmTokenClient(h.VmToken!);
        foreach (var agents in new object[]
        {
            new[] { new { tool = "vim", version = "9.1.0" } },
            new[] { new { tool = "claude", version = "latest; rm -rf /" } },
            new[] { new { tool = "claude", version = "1.0.0" }, new { tool = "claude", version = "1.0.1" } },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await vm.PostJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/usage", Report(agents))).StatusCode);
        Assert.Empty((await (await h.Client.GetAsync(Url)).ReadAsync<VmAgentsResponse>()).Agents);
    }
}
