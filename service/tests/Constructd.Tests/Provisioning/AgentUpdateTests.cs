using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;

namespace Constructd.Tests.Provisioning;

/// <summary>
/// Targeted coding-agent updates (<c>POST /vms/{name}/agents/{agent}/update</c> and
/// <c>POST /vms/{name}/agents/update</c>): updates only the chosen agent(s) without running <c>provision.sh</c>,
/// enforces owner-only auth and VM guards, holds the VM gate, starts a stopped VM first, and refreshes agent versions.
/// </summary>
public sealed class AgentUpdateTests
{
    private const string Vm = ProvisionHarness.Vm;

    private static AgentUpdateJobResult Result(JobResponse job) =>
        JsonSerializer.Deserialize<AgentUpdateJobResult>(JsonSerializer.Serialize(job.Result), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static async Task ReportAgentsAsync(ProvisionHarness h, params object[] agents)
    {
        using var vm = h.App.CreateVmTokenClient(h.VmToken!);
        var response = await vm.PostJsonAsync($"/api/v1/vms/{Vm}/usage",
            new { generatedAt = DateTimeOffset.UtcNow, days = Array.Empty<object>(), agents });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Single_agent_update_runs_only_that_agent_and_refreshes_agent_state()
    {
        await using var h = await ProvisionHarness.StartAsync();
        h.App.Service<FakeAgentReleases>().Latest["claude"] = "2.2.0";
        h.App.Service<FakeAgentReleases>().Latest["codex"] = "0.100.0";
        await ReportAgentsAsync(h,
            new { tool = "claude", version = "2.1.4", signedIn = true },
            new { tool = "codex", version = "0.98.0", signedIn = true });
        h.Ssh.AgentReport = """[{"tool":"claude","version":"2.1.4","signedIn":true},{"tool":"codex","version":"0.100.0","signedIn":true}]""";

        var response = await h.Client.PostAsync($"/api/v1/vms/{Vm}/agents/codex/update", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.ReadAsync<VmJobAcceptedResponse>();
        Assert.Equal(JobKinds.UpdateAgents, accepted.Kind);

        var job = await h.Client.WaitForJobAsync(accepted.JobId);
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(["codex"], Result(job).Agents);
        Assert.Equal(["codex"], Result(job).Updated);

        var commands = h.Ssh.Commands.Select(FakeSshRunner.Unwrap).ToArray();
        Assert.DoesNotContain(commands, c => c.Contains("bin/provision.sh", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, c => c.Contains("construct-t3-update.sh", StringComparison.Ordinal));
        var updateCmd = Assert.Single(commands, c => c.Contains("bin/install-ai-tools.sh", StringComparison.Ordinal));
        Assert.Contains("AI_TOOLS_OVERRIDE=codex", updateCmd);
        Assert.Contains("AI_CONSOLE_INTEGRATION=false", updateCmd);
        Assert.Contains(commands, c => c.Contains("construct-usage-report.sh", StringComparison.Ordinal) && c.Contains("--agents", StringComparison.Ordinal));

        var agents = await (await h.Client.GetAsync($"/api/v1/vms/{Vm}/agents")).ReadAsync<VmAgentsResponse>();
        Assert.Equal(new VmAgentResponse("codex", "Codex", "0.100.0", "0.100.0", false, true),
            Assert.Single(agents.Agents, a => a.Tool == "codex"));
        Assert.Equal(new VmAgentResponse("claude", "Claude Code", "2.1.4", "2.2.0", true, true),
            Assert.Single(agents.Agents, a => a.Tool == "claude"));
    }

    [Fact]
    public async Task Single_agent_update_maps_claude_and_uses_t3_updater_for_t3()
    {
        await using var h = await ProvisionHarness.StartAsync();

        var claudeRes = await h.Client.PostAsync($"/api/v1/vms/{Vm}/agents/claude/update", null);
        Assert.Equal(HttpStatusCode.Accepted, claudeRes.StatusCode);
        var claudeJob = await h.Client.WaitForJobAsync((await claudeRes.ReadAsync<VmJobAcceptedResponse>()).JobId);
        Assert.Equal(JobState.Succeeded, claudeJob.State);

        var claudeCmd = Assert.Single(h.Ssh.Commands.Select(FakeSshRunner.Unwrap), c => c.Contains("bin/install-ai-tools.sh", StringComparison.Ordinal));
        Assert.Contains("AI_TOOLS_OVERRIDE=claude-code", claudeCmd);
        Assert.Contains("AI_CONSOLE_INTEGRATION=false", claudeCmd);

        var t3Res = await h.Client.PostAsync($"/api/v1/vms/{Vm}/agents/t3/update", null);
        Assert.Equal(HttpStatusCode.Accepted, t3Res.StatusCode);
        var t3Job = await h.Client.WaitForJobAsync((await t3Res.ReadAsync<VmJobAcceptedResponse>()).JobId);
        Assert.Equal(JobState.Succeeded, t3Job.State);

        var commands = h.Ssh.Commands.Select(FakeSshRunner.Unwrap).ToArray();
        Assert.Single(commands, c => c.Contains("bin/construct-t3-update.sh", StringComparison.Ordinal));
        Assert.Single(commands, c => c.Contains("bin/install-ai-tools.sh", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, c => c.Contains("bin/provision.sh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_all_updates_only_behind_agents_in_one_job_with_t3_last()
    {
        await using var h = await ProvisionHarness.StartAsync();
        var latest = h.App.Service<FakeAgentReleases>().Latest;
        latest["claude"] = "2.2.0";
        latest["codex"] = "0.98.0";
        latest["opencode"] = "2.4.0";
        latest["t3"] = "0.2.0";
        await ReportAgentsAsync(h,
            new { tool = "t3", version = "0.1.0" },
            new { tool = "codex", version = "0.98.0", signedIn = true },
            new { tool = "claude", version = "2.1.4", signedIn = true },
            new { tool = "opencode", version = "2.3.2", signedIn = true });
        h.Ssh.AgentReport = """[{"tool":"claude","version":"2.2.0","signedIn":true},{"tool":"codex","version":"0.98.0","signedIn":true},{"tool":"opencode","version":"2.4.0","signedIn":true},{"tool":"t3","version":"0.2.0"}]""";

        var response = await h.Client.PostJsonAsync($"/api/v1/vms/{Vm}/agents/update", new { });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.ReadAsync<VmJobAcceptedResponse>();
        Assert.Equal(JobKinds.UpdateAgents, accepted.Kind);

        var job = await h.Client.WaitForJobAsync(accepted.JobId);
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(["claude", "opencode", "t3"], Result(job).Agents);
        Assert.Equal(["claude", "opencode", "t3"], Result(job).Updated);

        var updateCmds = h.Ssh.Commands.Select(FakeSshRunner.Unwrap)
            .Where(c => c.Contains("bin/install-ai-tools.sh", StringComparison.Ordinal) || c.Contains("bin/construct-t3-update.sh", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, updateCmds.Length);
        Assert.Contains("AI_TOOLS_OVERRIDE=claude-code", updateCmds[0]);
        Assert.Contains("AI_TOOLS_OVERRIDE=opencode", updateCmds[1]);
        Assert.Contains("bin/construct-t3-update.sh", updateCmds[2]);
        Assert.DoesNotContain(h.Ssh.Commands, c => c.Contains("bin/provision.sh", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Ssh.Commands, c => c.Contains("AI_TOOLS_OVERRIDE=codex", StringComparison.Ordinal));

        var agents = await (await h.Client.GetAsync($"/api/v1/vms/{Vm}/agents")).ReadAsync<VmAgentsResponse>();
        Assert.All(agents.Agents, a => Assert.False(a.UpdateAvailable));
    }

    [Fact]
    public async Task Update_all_returns_conflict_when_no_agents_are_behind()
    {
        await using var h = await ProvisionHarness.StartAsync();
        h.App.Service<FakeAgentReleases>().Latest["codex"] = "0.98.0";
        await ReportAgentsAsync(h, new { tool = "codex", version = "0.98.0" });

        var response = await h.Client.PostJsonAsync($"/api/v1/vms/{Vm}/agents/update", new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("agents-up-to-date", (await response.ReadAsync<JsonObject>())["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_and_invalid_agents_are_rejected_with_bad_request()
    {
        await using var h = await ProvisionHarness.StartAsync();

        foreach (var bad in new[] { "vim", "claude-code", "codex;id" })
            Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsync($"/api/v1/vms/{Vm}/agents/{Uri.EscapeDataString(bad)}/update", null)).StatusCode);

        foreach (var body in new object[]
        {
            new { agents = Array.Empty<string>() },
            new { agents = new[] { "vim" } },
            new { agents = new[] { "codex", "codex" } },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostJsonAsync($"/api/v1/vms/{Vm}/agents/update", body)).StatusCode);
    }

    [Fact]
    public async Task Only_the_vm_owner_can_update_agents_and_non_primary_or_deleting_vms_are_refused()
    {
        await using var h = await ProvisionHarness.StartAsync();
        using var bob = await h.App.CreateUserClientAsync("bob");
        using var admin = await h.App.CreateUserClientAsync("root", Role.Admin);
        using var vmToken = h.App.CreateVmTokenClient(h.VmToken!);

        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostAsync($"/api/v1/vms/{Vm}/agents/codex/update", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync($"/api/v1/vms/{Vm}/agents/codex/update", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await vmToken.PostAsync($"/api/v1/vms/{Vm}/agents/codex/update", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostJsonAsync($"/api/v1/vms/{Vm}/agents/update", new { agents = new[] { "codex" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.PostAsync("/api/v1/vms/missing/agents/codex/update", null)).StatusCode);

        // Child VM (non-primary) is refused.
        var child = (await h.App.Vms.GetAsync(Vm, default))! with { Name = "child-1", Kind = VmKind.Child, Parent = Vm };
        Assert.Equal(VmAddOutcome.Added, await h.App.Vms.AddAsync(child, 10, default));
        var childRes = await h.Client.PostAsync("/api/v1/vms/child-1/agents/codex/update", null);
        Assert.Equal(HttpStatusCode.Conflict, childRes.StatusCode);
        Assert.Equal("not-a-primary", (await childRes.ReadAsync<JsonObject>())["code"]!.GetValue<string>());

        // Deleting VM is refused.
        var deleting = (await h.App.Vms.GetAsync(Vm, default))! with { Deleting = true };
        Assert.True(await h.App.Vms.UpdateAsync(deleting, default));
        var delRes = await h.Client.PostAsync($"/api/v1/vms/{Vm}/agents/codex/update", null);
        Assert.Equal(HttpStatusCode.Conflict, delRes.StatusCode);
        Assert.Equal("vm-deleting", (await delRes.ReadAsync<JsonObject>())["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Agent_update_holds_the_vm_gate_while_running()
    {
        await using var h = await ProvisionHarness.StartAsync();
        h.Ssh.HoldAt = "agent-update";

        var first = await h.Client.PostAsync($"/api/v1/vms/{Vm}/agents/codex/update", null);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var jobId = (await first.ReadAsync<VmJobAcceptedResponse>()).JobId;
        await h.Ssh.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = await h.Client.PostAsync($"/api/v1/vms/{Vm}/agents/claude/update", null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await second.ReadAsync<JsonObject>();
        Assert.Equal("operation-in-progress", problem["code"]!.GetValue<string>());
        Assert.Equal(jobId, problem["jobId"]!.GetValue<string>());

        h.Ssh.Release.TrySetResult();
        var done = await h.Client.WaitForJobAsync(jobId);
        Assert.Equal(JobState.Succeeded, done.State);
    }

    [Fact]
    public async Task Agent_update_starts_a_stopped_vm_first_and_leaves_it_running()
    {
        await using var h = await ProvisionHarness.StartAsync();
        h.Ssh.Reachable = t => h.App.Driver.StateOf(t.Host.Split('.')[0]) == VmState.Running;
        (await h.Client.PostAsJsonAsync($"/api/v1/vms/{Vm}/power", new { action = "stop" })).EnsureSuccessStatusCode();
        Assert.Equal(VmState.Off, h.App.Driver.StateOf(Vm));

        var response = await h.Client.PostAsync($"/api/v1/vms/{Vm}/agents/codex/update", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = await h.Client.WaitForJobAsync((await response.ReadAsync<VmJobAcceptedResponse>()).JobId);

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Contains(job.Progress, p => p is { Step: "start", State: "succeeded" });
        Assert.Equal(VmState.Running, h.App.Driver.StateOf(Vm));
        Assert.Contains(h.Ssh.Commands.Select(FakeSshRunner.Unwrap), c => c.Contains("AI_TOOLS_OVERRIDE=codex", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failing_agent_update_reports_failure_and_still_refreshes_earlier_updates()
    {
        await using var h = await ProvisionHarness.StartAsync();
        h.Ssh.AgentUpdate = call => call.Command.Contains("AI_TOOLS_OVERRIDE=codex", StringComparison.Ordinal)
            ? (1, ["codex download failed"])
            : (0, ["claude updated"]);
        h.Ssh.AgentReport = """[{"tool":"claude","version":"2.2.0","signedIn":true},{"tool":"codex","version":"0.98.0","signedIn":true}]""";

        var response = await h.Client.PostJsonAsync($"/api/v1/vms/{Vm}/agents/update", new { agents = new[] { "claude", "codex" } });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = await h.Client.WaitForJobAsync((await response.ReadAsync<VmJobAcceptedResponse>()).JobId);

        Assert.Equal(JobState.Failed, job.State);
        var result = Result(job);
        Assert.False(result.Succeeded);
        Assert.Equal("codex", result.FailedStep);
        Assert.Equal("agent-update-failed", result.Code);
        Assert.Equal(["claude"], result.Updated);

        // Claude succeeded before Codex failed, so the agent report was still refreshed.
        var agents = await (await h.Client.GetAsync($"/api/v1/vms/{Vm}/agents")).ReadAsync<VmAgentsResponse>();
        Assert.Equal("2.2.0", Assert.Single(agents.Agents, a => a.Tool == "claude").Version);
    }
}
