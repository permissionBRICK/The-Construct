using System.Net;
using System.Text;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Provisioning;

/// <summary>
/// Appendix C.1 for <c>provision</c>: an engine killed in a step leaves a failed job naming that step,
/// and a retry runs from the start to success. Appendix C.2: after a successful and a failed run, no
/// secret is in the database, the log or any argument of a spawned process — raw or base64.
/// </summary>
public sealed class ProvisionDurabilityTests
{
    private static Dictionary<string, string?> Settings(string root) => new()
    {
        ["Constructd:HostAdmin:Source:RootDir"] = Path.Combine(root, "source"),
    };

    [Theory]
    [InlineData("copy", false)]
    [InlineData("provision", false)]
    [InlineData("project-commands", true)]
    [InlineData("applied", false)]
    public async Task An_engine_killed_in_a_step_leaves_a_failed_job_at_that_step_and_a_retry_succeeds(string step, bool defer)
    {
        var root = Path.Combine(Path.GetTempPath(), "construct-provision-kill-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "constructd.db");
        try
        {
            string jobId;
            var held = new FakeSshRunner { HoldAt = step };
            var first = await StartSqliteAsync(held, db, root, createVm: true);
            jobId = await first.StartAsync("click-1", defer: defer);
            await held.Held.Task.WaitAsync(TimeSpan.FromSeconds(20));
            // The kill: the host goes away with the job in the middle of the step, never released.
            await first.App.DisposeAsync();

            var second = await StartSqliteAsync(new FakeSshRunner(), db, root, createVm: false);
            try
            {
                // The fake hypervisor's memory went with the first host; the VM still runs.
                second.App.Driver.SetState(ProvisionHarness.Vm, VmState.Running);
                var interrupted = await second.Client.WaitForJobAsync(jobId);
                Assert.Equal(JobState.Failed, interrupted.State);
                Assert.Equal(step, interrupted.Phase);
                Assert.Contains("interrupted", interrupted.Error, StringComparison.Ordinal);

                // The same click again finds the stored job instead of starting a second run.
                var repeat = await (await second.PostAsync("click-1", defer: defer)).ReadAsync<VmJobAcceptedResponse>();
                Assert.Equal(jobId, repeat.JobId);
                Assert.True(repeat.Replayed);
                Assert.Empty(second.Ssh.Calls);

                // Retry: a new click, no settings from the client — the engine kept them.
                var response = await second.Client.PostJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/jobs",
                    new { kind = "provision", requestId = "click-2", commit = ProvisionHarness.Commit, deferProjectCommands = defer });
                Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
                var retry = await second.Client.WaitForJobAsync((await response.ReadAsync<VmJobAcceptedResponse>()).JobId);
                Assert.Equal(JobState.Succeeded, retry.State);
                Assert.Equal("true", FakeSshRunner.Variable(second.Ssh.Commands.Single(c => c.Contains("bin/provision.sh", StringComparison.Ordinal) && !c.Contains("PROVISION_PHASE", StringComparison.Ordinal)), "T3CODE"));
            }
            finally { await second.App.DisposeAsync(); }
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task No_secret_reaches_the_database_the_log_or_an_argument_on_success_and_on_failure()
    {
        var root = Path.Combine(Path.GetTempPath(), "construct-provision-secrets-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "constructd.db");
        const string token = "vm-token-secret-0123456789";
        const string sudo = "sudo-secret-9876543210";
        const string serveWeb = "serve-web-secret-24680";
        var tokenB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(token));
        var serveWebB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(serveWeb));
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes("https://bob:clone-secret-13579@git.example.test"));
        var guest = new FakeSshRunner();
        var processes = new GuestProcesses(guest);
        try
        {
            var h = await StartSqliteAsync(guest, db, root, createVm: true, new SystemSshRunner(processes));
            try
            {
                var secrets = new { vmTokenB64 = tokenB64, gitCloneCredentialsB64 = creds, serveWebTokenB64 = serveWebB64, sudoPassword = sudo };
                // A guest that prints what it was given.
                guest.Provision = _ => (0, [$"echo {token} {tokenB64} {serveWeb} clone-secret-13579 {sudo}", .. FakeSshRunner.Clean]);
                Assert.Equal(JobState.Succeeded, (await h.RunAsync("ok", secrets: secrets, defer: true)).State);
                guest.Provision = _ => (3, [$"echo {token}", .. FakeSshRunner.WithErrors(($"failed with {sudo}", 1, $"/var/log/{serveWeb}.log"))]);
                Assert.Equal(JobState.Failed, (await h.RunAsync("fails", secrets: secrets)).State);
                guest.Provision = _ => (1, [$"echo {tokenB64}"]);
                Assert.Equal(JobState.Failed, (await h.RunAsync("critical", secrets: secrets)).State);
            }
            finally { await h.App.DisposeAsync(); }

            string[] values = [token, tokenB64, sudo, serveWeb, serveWebB64, creds, "clone-secret-13579"];
            var forms = values.Concat(values.Select(v => Convert.ToBase64String(Encoding.UTF8.GetBytes(v)))).ToArray();
            var database = string.Concat(Directory.GetFiles(root, "constructd.db*").Select(f => Encoding.UTF8.GetString(File.ReadAllBytes(f))));
            Assert.Contains("provision", database, StringComparison.Ordinal);
            var logs = h.App.Logs.AllText();
            var argv = string.Join('\n', processes.Calls.SelectMany(c => c.Arguments));
            Assert.Contains("bin/provision.sh", argv, StringComparison.Ordinal);
            foreach (var form in forms)
            {
                Assert.DoesNotContain(form, database, StringComparison.Ordinal);
                Assert.DoesNotContain(form, logs, StringComparison.Ordinal);
                Assert.DoesNotContain(form, argv, StringComparison.Ordinal);
            }

            // They did reach the guest — on stdin.
            var stdin = string.Join('\n', processes.Calls.Select(c => c.StandardInput));
            Assert.Contains(tokenB64, stdin, StringComparison.Ordinal);
            Assert.Contains(sudo, stdin, StringComparison.Ordinal);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task A_create_with_secrets_stages_them_for_its_provision_and_stores_logs_or_audits_none()
    {
        var root = Path.Combine(Path.GetTempPath(), "construct-create-secrets-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "constructd.db");
        const string sudo = "create-sudo-secret-112233";
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes("https://bob:create-clone-secret-445566@git.example.test"));
        var guest = new FakeSshRunner();
        var processes = new GuestProcesses(guest);
        try
        {
            var h = await StartSqliteAsync(guest, db, root, createVm: true, new SystemSshRunner(processes));
            try
            {
                ((Fakes.FakeReleaseInfo)h.App.Service<IReleaseInfo>()).Installed = new(ProvisionHarness.Commit, "1.0.0", null, "release");
                guest.Provision = _ => (0, [$"echo {sudo} create-clone-secret-445566", .. FakeSshRunner.Clean]);
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/vms")
                {
                    Content = System.Net.Http.Json.JsonContent.Create(new
                    {
                        name = "fresh", cpu = 2, ramGb = 4, diskGb = 32, desired = ProvisionHarness.Desired("web"),
                        secrets = new { gitCloneCredentialsB64 = creds, sudoPassword = sudo },
                    }),
                };
                request.Headers.Add("X-Construct-Operation-Key", "create-click-1");
                var response = await h.Client.SendAsync(request);
                Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
                var job = await h.Client.WaitForJobAsync((await response.ReadAsync<JobAcceptedResponse>()).JobId);
                Assert.True(job.State == JobState.Succeeded, job.Error);
                Assert.DoesNotContain("create-clone-secret", System.Text.Json.JsonSerializer.Serialize(job), StringComparison.Ordinal);
                Assert.DoesNotContain(sudo, System.Text.Json.JsonSerializer.Serialize(job), StringComparison.Ordinal);
            }
            finally { await h.App.DisposeAsync(); }

            string[] values = [sudo, creds, "create-clone-secret-445566"];
            var forms = values.Concat(values.Select(v => Convert.ToBase64String(Encoding.UTF8.GetBytes(v)))).ToArray();
            var database = string.Concat(Directory.GetFiles(root, "constructd.db*").Select(f => Encoding.UTF8.GetString(File.ReadAllBytes(f))));
            Assert.Contains("create-click-1", database, StringComparison.Ordinal);
            Assert.Contains("vm.create", database, StringComparison.Ordinal);
            var logs = h.App.Logs.AllText();
            var argv = string.Join('\n', processes.Calls.SelectMany(c => c.Arguments));
            Assert.Contains("bin/provision.sh", argv, StringComparison.Ordinal);
            foreach (var form in forms)
            {
                Assert.DoesNotContain(form, database, StringComparison.Ordinal);
                Assert.DoesNotContain(form, logs, StringComparison.Ordinal);
                Assert.DoesNotContain(form, argv, StringComparison.Ordinal);
            }

            // They reached the new VM's provision on stdin, in the staged secret file.
            var stdin = string.Join('\n', processes.Calls.Select(c => c.StandardInput));
            Assert.Contains($"GIT_CLONE_CREDENTIALS_B64='{creds}'", stdin, StringComparison.Ordinal);
            Assert.Contains(sudo, stdin, StringComparison.Ordinal);
        }
        finally { TryDelete(root); }
    }

    private static async Task<ProvisionHarness> StartSqliteAsync(FakeSshRunner ssh, string db, string root, bool createVm, ISshRunner? runner = null)
    {
        var h = await ProvisionHarness.StartAsync(ssh, db, createVm, Settings(root), runner);
        return h;
    }

    private static void TryDelete(string root)
    {
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The process layer under the real <see cref="SystemSshRunner"/>: records every argument vector
    /// and stdin, and answers through the scripted guest.
    /// </summary>
    internal sealed class GuestProcesses(FakeSshRunner guest) : IProcessRunner
    {
        public System.Collections.Concurrent.ConcurrentQueue<Fakes.RecordedProcess> Calls { get; } = new();

        public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? standardInput, TimeSpan timeout,
            IProgress<string>? standardOutputLines, CancellationToken cancellationToken)
        {
            Calls.Enqueue(new(fileName, [.. arguments], standardInput, timeout));
            var target = new SshTarget("guest", 22, "agent", null, null);
            if (fileName == "scp")
            {
                await guest.CopyAsync(target, arguments[^2], arguments[^1].Split(':', 2)[1], cancellationToken);
                return new ProcessResult(0, "", "", false);
            }

            var output = new List<string>();
            var exit = await guest.RunAsync(target, arguments[^1], standardInput, new Collect(output, standardOutputLines), timeout, cancellationToken);
            return new ProcessResult(exit, string.Join('\n', output), "", false);
        }

        private sealed class Collect(List<string> lines, IProgress<string>? next) : IProgress<string>
        {
            public void Report(string value) { lines.Add(value); next?.Report(value); }
        }
    }
}
