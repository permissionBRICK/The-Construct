using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Api.SignIn;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Tests.Support;

namespace Constructd.Tests.Provisioning;

/// <summary>Signing in to a VM's coding agents (design 11.8, S34) with the scripted guest of fake mode.</summary>
public sealed class AgentSignInTests
{
    private const string Url = $"/api/v1/vms/{ProvisionHarness.Vm}/agents";

    private static FakeGuestSessionRunner Guests(ProvisionHarness h) => h.App.Service<FakeGuestSessionRunner>();

    private static async Task<T> Until<T>(Func<Task<T?>> probe, string what, int seconds = 20) where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await probe() is { } value) return value;
            await Task.Delay(25);
        }

        throw new TimeoutException("Timed out waiting for " + what);
    }

    private static async Task<AgentSignInResponse?> Read(HttpClient client, string agent = "claude-code")
    {
        var response = await client.GetAsync($"{Url}/{agent}/sign-in");
        return response.StatusCode == HttpStatusCode.OK ? await response.ReadAsync<AgentSignInResponse>() : null;
    }

    private static Task<AgentSignInResponse> Until(HttpClient client, Func<AgentSignInResponse, bool> state, string what, string agent = "claude-code") =>
        Until(async () => await Read(client, agent) is { } s && state(s) ? s : null, what);

    private static async Task<AgentSignInResponse> StartAsync(ProvisionHarness h, string agent = "claude-code")
    {
        var response = await h.Client.PostAsync($"{Url}/{agent}/sign-in", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await response.ReadAsync<AgentSignInResponse>();
    }

    [Fact]
    public async Task The_owner_signs_in_through_the_agents_own_terminal_on_the_vm()
    {
        await using var h = await ProvisionHarness.StartAsync();
        using var changes = h.App.Service<ChangeFeed>().Subscribe(null);
        var started = await StartAsync(h);
        Assert.Equal(("claude-code", "running"), (started.Agent, started.State));

        var shown = await Until(h.Client, s => s.Text.Contains("Paste code here", StringComparison.Ordinal), "the prompt");
        Assert.Contains("https://sign-in.example/claude-code", shown.Text, StringComparison.Ordinal);
        var guest = Assert.Single(Guests(h).Sessions);
        // As root through sudo -n; the agent is the only thing on the command line besides the session id.
        Assert.Equal($"sudo -n bash -lc 'python3 /opt/construct/repo/bin/construct-agent-sign-in.py --session {started.Id} claude-code' 2>&1", guest.Command);

        const string code = "abc-123 \"quoted\" $(touch /tmp/pwned)";
        Assert.Equal(HttpStatusCode.NoContent, (await h.Client.PostAsJsonAsync($"{Url}/claude-code/sign-in/input", new { text = code })).StatusCode);
        var line = Assert.Single(guest.Received);
        Assert.Equal(code, JsonDocument.Parse(line).RootElement.GetProperty("text").GetString());
        Assert.DoesNotContain(h.Ssh.Commands.Append(guest.Command), c => c.Contains("abc-123", StringComparison.Ordinal));

        var ended = await Until(h.Client, s => s.State == "ended", "the end");
        Assert.Equal(("done", true), (ended.Outcome, ended.SignedIn));
        // Over: what the terminal showed (its link, the code) is gone; the outcome and the check stay.
        Assert.Equal("", ended.Text);

        // The VM had reported no agents yet: the sign-in's check is there at once, with the version it read.
        var agents = await (await h.Client.GetAsync(Url)).ReadAsync<VmAgentsResponse>();
        var claude = Assert.Single(agents.Agents);
        Assert.Equal(("claude", "1.0.0", (bool?)true), (claude.Tool, claude.Version, claude.SignedIn));

        var seen = new List<Change>();
        while (changes.Live.TryRead(out var change)) seen.Add(change);
        var signIn = seen.Where(c => c.Type.StartsWith("agent-sign-in.", StringComparison.Ordinal)).ToList();
        Assert.All(signIn, c => Assert.Equal((started.Id, ProvisionHarness.Vm, "alice", true), (c.Id, c.Vm, c.Owner, c.OwnerOnly)));
        Assert.Equal("agent-sign-in.started", signIn[0].Type);
        Assert.Contains(signIn, c => c.Type == "agent-sign-in.output");
        Assert.Single(signIn, c => c.Type == "agent-sign-in.ended");
    }

    [Fact]
    public async Task A_sign_in_updates_the_agent_the_vm_reported()
    {
        await using var h = await ProvisionHarness.StartAsync();
        using var vm = h.App.CreateVmTokenClient(h.VmToken!);
        Assert.Equal(HttpStatusCode.NoContent, (await vm.PostJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/usage", new
        {
            generatedAt = DateTimeOffset.UtcNow, days = Array.Empty<object>(),
            agents = new object[] { new { tool = "codex", version = "0.170.0", signedIn = false } },
        })).StatusCode);
        await StartAsync(h, "codex");
        await Until(h.Client, s => s.Running(), "the prompt", "codex");
        await h.Client.PostAsJsonAsync($"{Url}/codex/sign-in/input", new { text = "" });
        await Until(h.Client, s => s.State == "ended", "the end", "codex");

        var codex = Assert.Single((await (await h.Client.GetAsync(Url)).ReadAsync<VmAgentsResponse>()).Agents);
        Assert.Equal(("0.170.0", (bool?)true), (codex.Version, codex.SignedIn));
    }

    [Fact]
    public async Task Cancel_ends_the_sign_in_and_stops_it_on_the_vm()
    {
        await using var h = await ProvisionHarness.StartAsync();
        var started = await StartAsync(h);
        await Until(h.Client, s => s.Text.Length > 0, "the prompt");
        Assert.Equal(HttpStatusCode.NoContent, (await h.Client.DeleteAsync($"{Url}/claude-code/sign-in")).StatusCode);

        var ended = await Read(h.Client);
        Assert.Equal(("ended", "stopped", ""), (ended!.State, ended.Outcome, ended.Text));
        Assert.True(Assert.Single(Guests(h).Sessions).Disposed);
        Assert.Contains(h.Ssh.Commands, c => c.Contains($"construct-agent-sign-in.py --stop --session {started.Id}", StringComparison.Ordinal));
        // Over: input is refused, a second cancel does nothing, and the agent's state is not touched.
        Assert.Equal(HttpStatusCode.Conflict, (await h.Client.PostAsJsonAsync($"{Url}/claude-code/sign-in/input", new { text = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Client.DeleteAsync($"{Url}/claude-code/sign-in")).StatusCode);
        Assert.Empty((await (await h.Client.GetAsync(Url)).ReadAsync<VmAgentsResponse>()).Agents);
    }

    [Fact]
    public async Task A_new_sign_in_on_the_vm_ends_the_one_before()
    {
        await using var h = await ProvisionHarness.StartAsync();
        var first = await StartAsync(h);
        await Until(h.Client, s => s.Text.Length > 0, "the prompt");
        var second = await StartAsync(h, "antigravity");

        Assert.NotEqual(first.Id, second.Id);
        Assert.True(Guests(h).Sessions.First().Disposed);
        Assert.Contains(h.Ssh.Commands, c => c.Contains($"--stop --session {first.Id}", StringComparison.Ordinal));
        // The VM's latest sign-in is Antigravity's now; Claude Code's is gone with it.
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync($"{Url}/claude-code/sign-in")).StatusCode);
        var shown = await Until(h.Client, s => s.Text.Length > 0, "the prompt", "antigravity");
        Assert.Contains("--session " + second.Id + " antigravity", Guests(h).Sessions.Last().Command, StringComparison.Ordinal);
        Assert.Contains("https://sign-in.example/antigravity", shown.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_owner_signs_in_not_an_admin_another_user_or_the_vm()
    {
        await using var h = await ProvisionHarness.StartAsync();
        await StartAsync(h);
        var bob = await h.App.CreateUserClientAsync("bob");
        var admin = await h.App.CreateUserClientAsync("root", Role.Admin);
        var vmToken = h.App.CreateVmTokenClient(h.VmToken!);
        foreach (var other in new[] { bob, admin, vmToken })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{Url}/claude-code/sign-in")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync($"{Url}/codex/sign-in", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"{Url}/claude-code/sign-in/input", new { text = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.DeleteAsync($"{Url}/claude-code/sign-in")).StatusCode);
        }

        Assert.Single(Guests(h).Sessions);
        Assert.Empty(Guests(h).Sessions.Single().Received);
        Assert.Equal("running", (await Read(h.Client))!.State);
    }

    [Fact]
    public async Task Input_is_text_or_one_key_short_and_without_control_characters()
    {
        await using var h = await ProvisionHarness.StartAsync();
        await StartAsync(h);
        await Until(h.Client, s => s.Text.Length > 0, "the prompt");
        var input = $"{Url}/claude-code/sign-in/input";
        foreach (var bad in new object[]
        {
            new { }, new { text = "a", key = "up" }, new { key = "left" }, new { text = "a\u001b[A" }, new { text = "line\nnext" },
            new { text = new string('x', 4097) },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsJsonAsync(input, bad)).StatusCode);
        Assert.Empty(Guests(h).Sessions.Single().Received);

        Assert.Equal(HttpStatusCode.NoContent, (await h.Client.PostAsJsonAsync(input, new { key = "down" })).StatusCode);
        Assert.Equal("""{"key":"down"}""", Guests(h).Sessions.Single().Received.Single());
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsync($"{Url}/gemini/sign-in", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.PostAsync("/api/v1/vms/ghost/agents/codex/sign-in", null)).StatusCode);
    }

    [Fact]
    public async Task What_the_terminal_shows_and_what_is_typed_stay_out_of_the_audit_log()
    {
        await using var h = await ProvisionHarness.StartAsync();
        await StartAsync(h);
        await Until(h.Client, s => s.Text.Length > 0, "the prompt");
        await h.Client.PostAsJsonAsync($"{Url}/claude-code/sign-in/input", new { text = "one-time-code-42" });
        await Until(h.Client, s => s.State == "ended", "the end");
        // A refused input is recorded, without its text.
        await h.Client.PostAsJsonAsync($"{Url}/claude-code/sign-in/input", new { text = "late-code-43" });

        var admin = await h.App.CreateUserClientAsync("root", Role.Admin);
        var entries = await (await admin.GetAsync("/api/v1/audit")).ReadAsync<List<AuditResponse>>();
        Assert.Contains(entries, e => e.Action == "vm.agent.sign-in" && e.Outcome == AuditOutcome.Success);
        Assert.Contains(entries, e => e.Action == "vm.agent.sign-in.input" && e.Outcome == AuditOutcome.Failure);
        Assert.DoesNotContain(entries, e => e.Action == "vm.agent.sign-in.input" && e.Outcome == AuditOutcome.Success);
        var all = JsonSerializer.Serialize(entries);
        Assert.DoesNotContain("one-time-code", all, StringComparison.Ordinal);
        Assert.DoesNotContain("late-code", all, StringComparison.Ordinal);
        Assert.DoesNotContain("sign-in.example", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_that_ends_without_an_end_line_is_an_error()
    {
        await using var h = await ProvisionHarness.StartAsync();
        var release = new TaskCompletionSource();
        Guests(h).SignInScript = async s =>
        {
            s.EmitRaw("sudo: a password is required");
            await release.Task;
        };
        await StartAsync(h, "opencode");
        // What the connection said shows while it lasts.
        var shown = await Until(h.Client, s => s.Text.Length > 0, "the note", "opencode");
        Assert.Contains("sudo: a password is required", shown.Text, StringComparison.Ordinal);
        release.SetResult();
        var ended = await Until(h.Client, s => s.State == "ended", "the end", "opencode");
        Assert.Equal(("error", "", (bool?)null), (ended.Outcome, ended.Text, ended.SignedIn));
    }
}

internal static class AgentSignInResponseExtensions
{
    public static bool Running(this AgentSignInResponse s) => s.State == "running" && s.Text.Length > 0;
}
