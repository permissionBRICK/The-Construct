using System.Net;
using System.Net.Http.Json;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Tests.Support;
using Constructd.Windows.Process;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Provisioning;

/// <summary>
/// The sign-in routes against a real guest over the system <c>ssh</c> (design 11.8): each installed agent's own
/// sign-in starts on the VM, its link shows, and a cancel stops it there. Nothing is signed in. The guest needs
/// <c>/opt/construct/repo/bin/construct-agent-sign-in.py</c>, the CLIs on root's login path and root over SSH or
/// sudo without a password. Runs only when a test guest is configured (<see cref="E2ESshFactAttribute"/>);
/// <c>CONSTRUCT_E2E_SIGN_IN_AGENTS</c> names the agents to try (default all four).
/// </summary>
public sealed class AgentSignInGuestE2ETests
{
    [E2ESshFact]
    public async Task Each_agents_sign_in_shows_its_link_and_a_cancel_stops_it_on_the_guest()
    {
        string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";
        var target = new SshTarget(Env("CONSTRUCT_E2E_SSH_HOST"), int.TryParse(Env("CONSTRUCT_E2E_SSH_PORT"), out var port) ? port : 22,
            Env("CONSTRUCT_E2E_SSH_USER") is { Length: > 0 } u ? u : "root", Env("CONSTRUCT_E2E_SSH_KEY") is { Length: > 0 } key ? key : null,
            Path.Combine(Path.GetTempPath(), "construct-e2e-known_hosts"));
        var agents = Env("CONSTRUCT_E2E_SIGN_IN_AGENTS") is { Length: > 0 } list ? list.Split(',') : CodingAgents.Known.ToArray();
        var ssh = new SystemSshRunner(new ProcessRunner());
        using var app = new TestApp(configureServices: services =>
        {
            services.AddSingleton<ISshRunner>(ssh);
            services.AddSingleton<IGuestSessionRunner>(new SshGuestSessionRunner(new StreamingProcessRunner()));
            services.AddSingleton<Constructd.Api.Jobs.IProvisionTargets>(new FixedTarget(target));
        });
        var client = await app.CreateUserClientAsync("alice");
        await client.CreateVmAsync("e2e");

        foreach (var agent in agents)
        {
            var url = $"/api/v1/vms/e2e/agents/{agent}/sign-in";
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync(url, null)).StatusCode);
            // OpenCode shows its list of integrations first: a search typed picks OpenAI, two arrow keys and Enter
            // its device code method.
            if (agent == "opencode")
            {
                await UntilAsync(client, url, s => s.Text.Contains("Select integration", StringComparison.Ordinal), "OpenCode's list");
                await InputAsync(client, url, new { text = "OpenAI" });
                await UntilAsync(client, url, s => s.Text.Contains("Select login method", StringComparison.Ordinal), "OpenCode's methods");
                await InputAsync(client, url, new { key = "down" });
                await InputAsync(client, url, new { key = "down" });
                await InputAsync(client, url, new { text = "" });
            }

            var shown = await UntilAsync(client, url, s => s.Text.Contains("https://", StringComparison.Ordinal), agent + "'s link");
            Assert.Equal("running", shown.State);

            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(url)).StatusCode);
            var ended = await (await client.GetAsync(url)).ReadAsync<AgentSignInResponse>();
            Assert.Equal(("ended", "stopped"), (ended.State, ended.Outcome));
        }

        // Nothing of any sign-in is left on the guest.
        var left = new List<string>();
        await ssh.RunAsync(target, (target.IsRoot ? "" : "sudo -n ") + "pgrep -af 'auth login|login --device-auth|acp --login|construct-agent-sign-in'",
            null, new SyncProgress(left.Add), TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.DoesNotContain(left, l => !l.Contains("pgrep", StringComparison.Ordinal));
    }

    private static async Task InputAsync(HttpClient client, string url, object input)
    {
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(url + "/input", input)).StatusCode);
        await Task.Delay(500);
    }

    private static async Task<AgentSignInResponse> UntilAsync(HttpClient client, string url, Func<AgentSignInResponse, bool> done, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(90);
        AgentSignInResponse? last = null;
        while (DateTime.UtcNow < until)
        {
            var response = await client.GetAsync(url);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                last = await response.ReadAsync<AgentSignInResponse>();
                if (done(last)) return last;
                if (last.State == "ended") break;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Timed out waiting for {what}; the terminal showed: {last?.Text}");
    }

    private sealed class SyncProgress(Action<string> add) : IProgress<string>
    {
        public void Report(string value) { lock (this) add(value); }
    }

    private sealed class FixedTarget(SshTarget target) : Constructd.Api.Jobs.IProvisionTargets
    {
        public Task<SshTarget?> ResolveAsync(Vm vm, CancellationToken ct) => Task.FromResult<SshTarget?>(target);
        public string? BootstrapPublicKey() => null;
        public Task ForgetHostKeyAsync(SshTarget target, CancellationToken ct) => Task.CompletedTask;
    }
}
