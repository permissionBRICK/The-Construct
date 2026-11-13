using System.Net;
using System.Text;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Tests.Support;

namespace Constructd.Tests.Provisioning;

/// <summary>The <c>provision</c> job end to end against a scripted guest (design 7.1, 7.3, 7.5).</summary>
public sealed class ProvisionJobTests
{
    [Fact]
    public async Task A_clean_run_copies_the_source_runs_provision_sh_and_records_the_applied_state()
    {
        await using var h = await ProvisionHarness.StartAsync();
        var job = await h.RunAsync("click-1");

        Assert.True(JobState.Succeeded == job.State, job.Error + "\n" + string.Join("\n", job.Progress.Select(p => p.Step + " " + p.State + " " + p.Text)));
        var result = ProvisionHarness.Result(job);
        Assert.True(result.Succeeded);
        Assert.Empty(result.FailedSteps);
        Assert.Equal(ProvisionHarness.PayloadHash, result.PayloadHash);
        Assert.Equal(ProvisionHarness.Commit, result.Applied?.Commit);
        Assert.Equal(result.DesiredHash, result.Applied?.DesiredHash);
        Assert.Equal("main", result.Applied?.Channel);
        Assert.True(result.Rebooted); // the bootstrap path (not root) always reboots, as the PowerShell does

        // Every step reported, in order, with structured progress.
        var steps = job.Progress.Where(p => p.State == "running" && p.Text == $"{p.Step}: running").Select(p => p.Step!).ToArray();
        Assert.Equal(["source", "copy", "provision", "project-commands", "applied", "finish"], steps);
        Assert.Contains(job.Progress, p => p.Step == "provision" && p.Text == "installing...");
        Assert.Contains(job.Progress, p => p.Step == "project-commands" && p.State == "skipped");

        // The archive is the PowerShell's: top directory stripped, modes kept, the revision marker.
        Assert.Contains("bin/provision.sh|755", h.Ssh.CopiedTarEntries);
        Assert.Contains("README.md|644", h.Ssh.CopiedTarEntries);
        Assert.Contains("docs|755", h.Ssh.CopiedTarEntries);
        Assert.Single(h.Ssh.CopiedTarEntries, e => e.StartsWith(".construct-revision|", StringComparison.Ordinal));
        var commands = h.Ssh.Commands.ToList();
        Assert.Contains(commands, c => c.Contains("tar -xzf /tmp/construct-repo.tar.gz -C /opt/construct/repo && chown -R agent:agent /opt/construct", StringComparison.Ordinal));
        Assert.Contains(commands, c => c.Contains("sleep 3; reboot", StringComparison.Ordinal));
        var provision = commands.Single(c => c.Contains("bin/provision.sh", StringComparison.Ordinal));
        Assert.StartsWith("sudo -n env TERM=xterm-256color FORCE_COLOR=1 CLICOLOR_FORCE=1 DEBIAN_FRONTEND=noninteractive bash -lc ", provision, StringComparison.Ordinal);
        Assert.Equal("true", FakeSshRunner.Variable(provision, "T3CODE"));
        Assert.Equal("default", FakeSshRunner.Variable(provision, "PROJECTS"));
        Assert.Equal("devbox", FakeSshRunner.Variable(provision, "CONSTRUCT_INSTANCE_NAME"));
        Assert.Equal("2201", FakeSshRunner.Variable(provision, "CONSTRUCT_EXTERNAL_SSH_PORT"));
        Assert.Equal("true", FakeSshRunner.Variable(provision, "REBOOT_AFTER_PROVISION"));

        // The three facts: nothing waits right after a run.
        var status = await (await h.Client.GetAsync($"/api/v1/vms/{ProvisionHarness.Vm}/provision-status")).ReadAsync<ProvisionStatusResponse>();
        Assert.False(status.SettingsChanged);
        Assert.False(status.HardwareChanged);
    }

    [Fact]
    public async Task A_reprovision_needs_no_client_settings_and_a_change_shows_as_pending()
    {
        await using var h = await ProvisionHarness.StartAsync();
        Assert.Equal(JobState.Succeeded, (await h.RunAsync("first")).State);

        // Same settings again, from the engine's store.
        var again = await h.Client.WaitForJobAsync((await (await h.PostAsync("second")).ReadAsync<VmJobAcceptedResponse>()).JobId);
        Assert.Equal(JobState.Succeeded, again.State);
        Assert.Equal(2, h.Ssh.Commands.Count(c => c.Contains("bin/provision.sh", StringComparison.Ordinal)));
        Assert.All(h.Ssh.Commands.Where(c => c.Contains("bin/provision.sh", StringComparison.Ordinal)),
            c => Assert.Equal("true", FakeSshRunner.Variable(c, "T3CODE")));

        // A new desired state is stored: the status says it waits for the next reprovision.
        var vm = (await h.App.Vms.GetAsync(ProvisionHarness.Vm, default))!;
        await h.App.Service<Constructd.Api.Jobs.PrimaryCpuSettings>().SaveAsync(vm, 2, "alice", default);
        var store = h.App.Service<IVmDesiredStateSource>();
        var stored = (await store.GetDesiredAsync(ProvisionHarness.Vm, default))!;
        await store.SetDesiredAsync(ProvisionHarness.Vm, stored with { Hash = new string('e', 64) }, default);
        var status = await (await h.Client.GetAsync($"/api/v1/vms/{ProvisionHarness.Vm}/provision-status")).ReadAsync<ProvisionStatusResponse>();
        Assert.True(status.SettingsChanged);
        Assert.True(status.HardwareChanged);
        Assert.Equal(2, status.PendingCpu);
    }

    [Fact]
    public async Task Without_stored_settings_the_request_must_carry_them()
    {
        await using var h = await ProvisionHarness.StartAsync();
        var response = await h.Client.PostJsonAsync("/api/v1/vms/devbox/jobs", new { kind = "provision", requestId = "x1", commit = ProvisionHarness.Commit });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("desired-state-missing", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Optional_step_failures_fail_the_job_with_the_steps_and_the_fix_prompt_after_the_run_finished()
    {
        var ssh = new FakeSshRunner { Provision = _ => (3, FakeSshRunner.WithErrors(("Installing SDKs", 2, "/var/log/construct/provision/step-0-Installing-SDKs.log"))) };
        await using var h = await ProvisionHarness.StartAsync(ssh);
        var job = await h.RunAsync("click-1");

        Assert.Equal(JobState.Failed, job.State);
        Assert.StartsWith("provision-steps-failed", job.Error, StringComparison.Ordinal);
        var result = ProvisionHarness.Result(job);
        Assert.False(result.Succeeded);
        Assert.Equal("provision-steps-failed", result.Code);
        var step = Assert.Single(result.FailedSteps);
        Assert.Equal(new ProvisionStepError("Installing SDKs", 2, "/var/log/construct/provision/step-0-Installing-SDKs.log"), step);
        Assert.Equal([
            "On the last Construct provisioning run, the following step(s) failed:",
            "- Step 'Installing SDKs' failed (exit 2); log: /var/log/construct/provision/step-0-Installing-SDKs.log",
            "Read the provisioning log at /var/log/construct/provision/step-0-Installing-SDKs.log and diagnose and fix the underlying problem.",
        ], result.FixPrompt);
        // As in the PowerShell, the run went on: applied state read, reboot issued.
        Assert.NotNull(result.Applied);
        Assert.True(result.Rebooted);
    }

    [Theory]
    [InlineData(1, true, "provision-critical-failure")]
    [InlineData(0, false, "provision-result-missing")]
    [InlineData(3, true, "provision-result-inconsistent")]
    public async Task A_critical_failure_or_a_bad_result_block_fails_at_the_provision_step(int exit, bool withBlock, string code)
    {
        var lines = withBlock ? (exit == 1 ? FakeSshRunner.WithErrors(("Checking root privileges", 1, "")) : FakeSshRunner.Clean) : ["no sentinel here"];
        var ssh = new FakeSshRunner { Provision = _ => (exit, lines) };
        await using var h = await ProvisionHarness.StartAsync(ssh);
        var job = await h.RunAsync("click-1");

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("provision", job.Phase);
        var result = ProvisionHarness.Result(job);
        Assert.Equal(code, result.Code);
        Assert.Equal("provision", result.FailedStep);
        Assert.False(result.Rebooted);
        Assert.DoesNotContain(h.Ssh.Commands, c => c.Contains("reboot", StringComparison.Ordinal));
        if (exit == 1) Assert.Equal("Checking root privileges", Assert.Single(result.FailedSteps).Title);
    }

    [Fact]
    public async Task The_deferred_project_phase_runs_after_provision_sh_and_its_failures_join_the_result()
    {
        var ssh = new FakeSshRunner { ProjectPhase = _ => (7, ["boom"]) };
        await using var h = await ProvisionHarness.StartAsync(ssh);
        var job = await h.RunAsync("click-1", defer: true);

        Assert.Equal(JobState.Failed, job.State);
        var main = h.Ssh.Commands.First(c => c.Contains("bin/provision.sh", StringComparison.Ordinal));
        Assert.Equal("true", FakeSshRunner.Variable(main, "DEFER_PROJECT_COMMANDS"));
        var phase = h.Ssh.Commands.Single(c => c.Contains("PROVISION_PHASE", StringComparison.Ordinal));
        Assert.Equal("project-commands", FakeSshRunner.Variable(phase, "PROVISION_PHASE"));
        Assert.Equal("true", FakeSshRunner.Variable(phase, "CHECKOUT_PROJECTS"));
        Assert.Equal(new ProvisionStepError(ProvisionScript.ProjectCommandsTitle, 7, ""), Assert.Single(ProvisionHarness.Result(job).FailedSteps));
    }

    [Fact]
    public async Task An_ssh_failure_after_reported_errors_keeps_the_step_and_the_failed_steps()
    {
        var ssh = new ThrowingOnMarker { Provision = _ => (3, FakeSshRunner.WithErrors(("Installing SDKs", 2, "/l.log"))) };
        await using var h = await ProvisionHarness.StartAsync(ssh);
        var job = await h.RunAsync("click-1");
        Assert.Equal(JobState.Failed, job.State);
        var result = ProvisionHarness.Result(job);
        Assert.Equal("applied", result.FailedStep);
        Assert.Equal("Installing SDKs", Assert.Single(result.FailedSteps).Title);
        Assert.NotEmpty(result.FixPrompt);
    }

    private sealed class ThrowingOnMarker : FakeSshRunner
    {
        public override Task<int> RunAsync(SshTarget target, string command, string? standardInput, IProgress<string>? lines, TimeSpan timeout, CancellationToken ct) =>
            command.Contains("provisioned.env", StringComparison.Ordinal)
                ? throw new Constructd.Core.Services.SshRunnerException("ssh timed out.")
                : base.RunAsync(target, command, standardInput, lines, timeout, ct);
    }

    [Fact]
    public async Task A_failed_copy_fails_the_copy_step()
    {
        await using var h = await ProvisionHarness.StartAsync(new FakeSshRunner { UnpackExit = 2 });
        var job = await h.RunAsync("click-1");
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("copy", job.Phase);
        Assert.Equal("copy-failed", ProvisionHarness.Result(job).Code);
        Assert.DoesNotContain(h.Ssh.Commands, c => c.Contains("bin/provision.sh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sudo_without_a_password_fails_with_a_missing_secret_code()
    {
        await using var h = await ProvisionHarness.StartAsync(new FakeSshRunner { SudoPrompt = "sudo: a password is required" });
        var job = await h.RunAsync("click-1");
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("secret-missing", ProvisionHarness.Result(job).Code);
        Assert.Contains("sudoPassword", job.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repeated_request_id_returns_the_running_job_and_a_second_job_on_the_vm_is_refused()
    {
        var ssh = new FakeSshRunner { HoldAt = "provision" };
        await using var h = await ProvisionHarness.StartAsync(ssh);
        var first = await h.StartAsync("click-1");
        await ssh.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var repeat = await h.PostAsync("click-1", ProvisionHarness.Desired());
        Assert.Equal(HttpStatusCode.Accepted, repeat.StatusCode);
        var replayed = await repeat.ReadAsync<VmJobAcceptedResponse>();
        Assert.Equal(first, replayed.JobId);
        Assert.True(replayed.Replayed);

        var other = await h.PostAsync("click-2", ProvisionHarness.Desired());
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Contains("operation-in-progress", await other.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // A lifecycle job on the same VM is refused too.
        var restart = await h.Client.PostJsonAsync("/api/v1/vms/devbox/lifecycle", new { action = "restart" });
        Assert.Equal(HttpStatusCode.Conflict, restart.StatusCode);

        ssh.Release.TrySetResult();
        Assert.Equal(JobState.Succeeded, (await h.Client.WaitForJobAsync(first)).State);
        Assert.Single(h.Ssh.Commands, c => c.Contains("bin/provision.sh", StringComparison.Ordinal));
        Assert.Equal(JobState.Succeeded, (await h.RunAsync("click-2")).State);
    }

    [Fact]
    public async Task Cancellation_takes_effect_at_the_next_step_boundary()
    {
        var ssh = new FakeSshRunner { HoldAt = "provision" };
        await using var h = await ProvisionHarness.StartAsync(ssh);
        var id = await h.StartAsync("click-1");
        await ssh.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var cancel = await h.Client.PostAsync($"/api/v1/jobs/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        ssh.Release.TrySetResult();
        var job = await h.Client.WaitForJobAsync(id);

        Assert.Equal(JobState.Cancelled, job.State);
        // The running step finished; nothing after it ran.
        Assert.Equal("provision", job.Phase);
        Assert.Contains(job.Progress, p => p.Step == "provision" && p.State == "succeeded");
        Assert.DoesNotContain(h.Ssh.Commands, c => c.Contains("provisioned.env", StringComparison.Ordinal) || c.Contains("reboot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Requests_are_validated_before_a_job_exists()
    {
        await using var h = await ProvisionHarness.StartAsync();
        foreach (var body in new object[]
        {
            new { kind = "rebuild", requestId = "a", desired = ProvisionHarness.Desired() },
            new { kind = "provision", requestId = "bad id", desired = ProvisionHarness.Desired() },
            new { kind = "provision", requestId = "a", commit = "main", desired = ProvisionHarness.Desired() },
            new { kind = "provision", requestId = "a", desired = new { projects = new[] { "a,b" }, settings = new { } } },
            new { kind = "provision", requestId = "a", desired = new { projects = new[] { "x" }, settings = new { seedUser = "a b" } } },
            new { kind = "provision", requestId = "a", desired = ProvisionHarness.Desired(), secrets = new { vmTokenB64 = "not base64!" } },
        })
        {
            var response = await h.Client.PostJsonAsync("/api/v1/vms/devbox/jobs", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var stranger = await h.App.CreateUserClientAsync("mallory");
        var refused = await stranger.PostJsonAsync("/api/v1/vms/devbox/jobs", new { kind = "provision", requestId = "a", desired = ProvisionHarness.Desired() });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Empty(h.Ssh.Calls);
    }

    [Fact]
    public async Task Secrets_reach_the_guest_only_on_stdin_and_never_a_progress_line()
    {
        const string token = "vm-token-value-1234567890";
        const string sudo = "sudo-pass-0987654321";
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes("https://bob:clone-token-55555@git.example.test"));
        var tokenB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(token));
        var ssh = new FakeSshRunner
        {
            // A guest that echoes what it was given, in every form.
            Provision = call => (3, [$"token {tokenB64}", $"decoded {token}", "cred clone-token-55555", $"sudo {sudo}",
                .. FakeSshRunner.WithErrors(($"step with {token}", 1, ""))]),
        };
        await using var h = await ProvisionHarness.StartAsync(ssh);
        var job = await h.RunAsync("click-1", secrets: new { vmTokenB64 = tokenB64, gitCloneCredentialsB64 = creds, sudoPassword = sudo });

        Assert.Equal(JobState.Failed, job.State);
        var values = new[] { token, tokenB64, creds, "clone-token-55555", sudo };
        var stored = string.Join('\n', job.Progress.Select(p => p.Text)) + job.Error + System.Text.Json.JsonSerializer.Serialize(job.Result);
        foreach (var value in values) Assert.DoesNotContain(value, stored, StringComparison.Ordinal);
        Assert.Contains("[secret]", stored, StringComparison.Ordinal);

        // Never in an argument; staged over stdin, then sourced and removed by the root shell.
        foreach (var call in h.Ssh.Calls)
            foreach (var value in values) Assert.DoesNotContain(value, call.Command, StringComparison.Ordinal);
        var stage = h.Ssh.Calls.Single(c => c.Command.StartsWith("umask 077; cat > /tmp/.construct-provision-secrets.", StringComparison.Ordinal));
        Assert.Contains($"CONSTRUCT_VM_TOKEN_B64='{tokenB64}'", stage.StandardInput, StringComparison.Ordinal);
        Assert.Contains($"GIT_CLONE_CREDENTIALS_B64='{creds}'", stage.StandardInput, StringComparison.Ordinal);
        var provision = h.Ssh.Calls.Single(c => c.Command.Contains("bin/provision.sh", StringComparison.Ordinal));
        Assert.StartsWith("sudo -S -p '' env", provision.Command, StringComparison.Ordinal);
        Assert.Equal(sudo + "\n", provision.StandardInput);
        var file = stage.Command["umask 077; cat > ".Length..];
        Assert.Contains($"trap 'rm -f {file}' EXIT; set -a; . {file}; set +a;", FakeSshRunner.Unwrap(provision.Command), StringComparison.Ordinal);
        Assert.Null(FakeSshRunner.Variable(provision.Command, "CONSTRUCT_VM_TOKEN_B64"));
        Assert.Null(FakeSshRunner.Variable(provision.Command, "GIT_CLONE_CREDENTIALS_B64"));
        Assert.Equal("", FakeSshRunner.Variable(provision.Command, "VSCODE_SERVE_WEB_TOKEN_B64"));

        foreach (var value in values) Assert.DoesNotContain(value, h.App.Logs.AllText(), StringComparison.Ordinal);
    }
}
