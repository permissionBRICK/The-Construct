using System.Net;
using System.Net.Http.Json;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Tests.Support;

namespace Constructd.Tests.Provisioning;

/// <summary>
/// A lost sign-in (S36): a coding agent the VM reported signed in and then signed out gets a notice for its owner, once
/// per loss, until it is signed in again or dismissed.
/// </summary>
public sealed class LostSignInTests
{
    private const string Url = $"/api/v1/vms/{ProvisionHarness.Vm}/agents";

    private static async Task ReportAsync(ProvisionHarness h, params (string Tool, bool? SignedIn)[] agents)
    {
        using var vm = h.App.CreateVmTokenClient(h.VmToken!);
        var posted = await vm.PostJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/usage", new
        {
            generatedAt = DateTimeOffset.UtcNow, days = Array.Empty<object>(),
            agents = agents.Select(a => new { tool = a.Tool, version = "1.0.0", signedIn = a.SignedIn }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.NoContent, posted.StatusCode);
    }

    private static async Task<IReadOnlyList<AgentNoticeResponse>> NoticesAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/v1/agent-notices")).ReadAsync<AgentNoticesResponse>()).Notices;

    private static async Task<VmAgentResponse> AgentAsync(ProvisionHarness h, string tool) =>
        (await (await h.Client.GetAsync(Url)).ReadAsync<VmAgentsResponse>()).Agents.Single(a => a.Tool == tool);

    [Fact]
    public async Task A_sign_in_lost_after_it_was_signed_in_gets_one_notice_until_signed_in_again()
    {
        await using var h = await ProvisionHarness.StartAsync();
        using var changes = h.App.Service<ChangeFeed>().Subscribe(null);
        // Never signed in: nothing was lost.
        await ReportAsync(h, ("claude", false), ("codex", true));
        Assert.Empty(await NoticesAsync(h.Client));

        await ReportAsync(h, ("claude", true), ("codex", false));
        var notice = Assert.Single(await NoticesAsync(h.Client));
        Assert.Equal((ProvisionHarness.Vm, "codex", "Codex"), (notice.Vm, notice.Agent, notice.Name));
        Assert.Equal(notice.At, (await AgentAsync(h, "codex")).LostAt);
        Assert.Null((await AgentAsync(h, "claude")).LostAt);

        // Still signed out at the next report: the same notice; a check that did not answer changes nothing.
        await ReportAsync(h, ("claude", null), ("codex", false));
        Assert.Equal(notice.At, Assert.Single(await NoticesAsync(h.Client)).At);
        Assert.Equal(true, (await AgentAsync(h, "claude")).SignedIn);

        // Signed in again: gone. Lost again: back.
        await ReportAsync(h, ("claude", true), ("codex", true));
        Assert.Empty(await NoticesAsync(h.Client));
        await ReportAsync(h, ("claude", false), ("codex", true));
        Assert.Equal("claude-code", Assert.Single(await NoticesAsync(h.Client)).Agent);

        var seen = new List<Change>();
        while (changes.Live.TryRead(out var change)) seen.Add(change);
        var notices = seen.Where(c => c.Type == "agent-notice.changed").ToList();
        Assert.Equal(["codex", "codex", "claude"], notices.Select(c => c.Id));
        Assert.All(notices, c => Assert.Equal((ProvisionHarness.Vm, "alice", true), (c.Vm, c.Owner, c.OwnerOnly)));
    }

    [Fact]
    public async Task While_a_job_works_on_the_vm_a_signed_out_report_is_no_lost_sign_in_and_the_next_report_after_it_decides()
    {
        await using var h = await ProvisionHarness.StartAsync();
        await ReportAsync(h, ("claude", true), ("codex", true));

        // A reinstall (any job holding the VM) rebuilds it: its fresh guest reports every agent signed out (F35).
        await using (await h.App.Service<IVmOperationGate>().AcquireAsync(ProvisionHarness.Vm, "job-1", default))
        {
            await ReportAsync(h, ("claude", false), ("codex", false));
            Assert.Empty(await NoticesAsync(h.Client));
            Assert.Equal(true, (await AgentAsync(h, "claude")).SignedIn);
        }

        // After the job: the restore brought Claude back; Codex is really gone.
        await ReportAsync(h, ("claude", true), ("codex", false));
        Assert.Equal("codex", Assert.Single(await NoticesAsync(h.Client)).Agent);
    }

    [Fact]
    public async Task An_agent_a_report_leaves_out_during_a_job_keeps_its_sign_in_so_a_loss_after_the_job_is_still_told()
    {
        await using var h = await ProvisionHarness.StartAsync();
        await ReportAsync(h, ("claude", true), ("codex", true));

        // Mid-provision Claude's check does not answer: the report names Codex only.
        await using (await h.App.Service<IVmOperationGate>().AcquireAsync(ProvisionHarness.Vm, "job-1", default))
            await ReportAsync(h, ("codex", true));
        Assert.Equal(true, (await AgentAsync(h, "claude")).SignedIn);

        await ReportAsync(h, ("claude", false), ("codex", true));
        Assert.Equal("claude-code", Assert.Single(await NoticesAsync(h.Client)).Agent);
    }

    [Fact]
    public async Task A_dismissed_notice_stays_away_until_the_agent_was_signed_in_and_lost_again()
    {
        await using var h = await ProvisionHarness.StartAsync();
        await ReportAsync(h, ("claude", true));
        await ReportAsync(h, ("claude", false));
        Assert.Single(await NoticesAsync(h.Client));

        // Only the owner puts it away.
        var bob = await h.App.CreateUserClientAsync("bob");
        var admin = await h.App.CreateUserClientAsync("root", Role.Admin);
        foreach (var other in new[] { bob, admin })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await other.DeleteAsync($"{Url}/claude-code/notice")).StatusCode);
            Assert.Empty(await NoticesAsync(other));
        }

        Assert.Equal(HttpStatusCode.NoContent, (await h.Client.DeleteAsync($"{Url}/claude-code/notice")).StatusCode);
        Assert.Empty(await NoticesAsync(h.Client));
        Assert.Equal(HttpStatusCode.NoContent, (await h.Client.DeleteAsync($"{Url}/claude-code/notice")).StatusCode);
        await ReportAsync(h, ("claude", false));
        Assert.Empty(await NoticesAsync(h.Client));
        await ReportAsync(h, ("claude", true));
        await ReportAsync(h, ("claude", false));
        Assert.Single(await NoticesAsync(h.Client));
    }

    [Fact]
    public async Task A_sign_in_through_the_engine_clears_the_notice()
    {
        await using var h = await ProvisionHarness.StartAsync();
        await ReportAsync(h, ("claude", true));
        await ReportAsync(h, ("claude", false));
        var started = await (await h.Client.PostAsync($"{Url}/claude-code/sign-in", null)).ReadAsync<AgentSignInResponse>();
        var until = DateTime.UtcNow.AddSeconds(20);
        while ((await (await h.Client.GetAsync($"{Url}/claude-code/sign-in")).ReadAsync<AgentSignInResponse>()).Text.Length == 0 && DateTime.UtcNow < until)
            await Task.Delay(25);
        await h.Client.PostAsJsonAsync($"{Url}/claude-code/sign-in/input", new { text = "code", session = started.Id });
        while ((await (await h.Client.GetAsync($"{Url}/claude-code/sign-in")).ReadAsync<AgentSignInResponse>()).State != "ended" && DateTime.UtcNow < until)
            await Task.Delay(25);
        Assert.Empty(await NoticesAsync(h.Client));
        Assert.Null((await AgentAsync(h, "claude")).LostAt);
    }

    [Fact]
    public async Task Only_an_agent_the_vm_is_meant_to_have_gets_a_notice_and_one_gone_from_the_vm_loses_it()
    {
        await using var h = await ProvisionHarness.StartAsync();
        await h.Client.PutJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/desired",
            new { projects = Array.Empty<string>(), settings = new { }, codingAgents = new[] { "claude-code" } });
        await ReportAsync(h, ("claude", true), ("codex", true));
        await ReportAsync(h, ("claude", false), ("codex", false));
        Assert.Equal("claude-code", Assert.Single(await NoticesAsync(h.Client)).Agent);

        await ReportAsync(h, ("codex", false));
        Assert.Empty(await NoticesAsync(h.Client));
    }
}
