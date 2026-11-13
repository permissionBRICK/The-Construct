using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Constructd.Api.Contracts;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Constructd.Windows.Process;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Provisioning;

/// <summary>Runs only when a test guest is configured: <c>CONSTRUCT_E2E_SSH_HOST</c>, <c>_PORT</c>, <c>_USER</c>, <c>_KEY</c>.</summary>
public sealed class E2ESshFactAttribute : FactAttribute
{
    public E2ESshFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONSTRUCT_E2E_SSH_HOST")))
            Skip = "No test guest: set CONSTRUCT_E2E_SSH_HOST, _PORT, _USER and _KEY.";
    }
}

/// <summary>
/// The real <c>provision</c> job against a real guest over the system <c>ssh</c>: this checkout's
/// tree (<c>git archive HEAD</c>) as the cached source, <c>bin/provision.sh</c> for real. Optional:
/// <c>CONSTRUCT_E2E_SUDO_PASSWORD</c> when the user needs one for sudo, <c>CONSTRUCT_E2E_SEED_USER</c>
/// when the seed user is not the SSH user (default: the SSH user, or <c>agent</c> for root).
/// </summary>
public sealed class ProvisionGuestE2ETests
{
    [E2ESshFact]
    public async Task The_real_job_provisions_a_real_guest()
    {
        string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";
        var user = Env("CONSTRUCT_E2E_SSH_USER") is { Length: > 0 } u ? u : "root";
        var target = new SshTarget(Env("CONSTRUCT_E2E_SSH_HOST"), int.TryParse(Env("CONSTRUCT_E2E_SSH_PORT"), out var port) ? port : 22,
            user, Env("CONSTRUCT_E2E_SSH_KEY") is { Length: > 0 } key ? key : null,
            Path.Combine(Path.GetTempPath(), "construct-e2e-known_hosts"));
        var seed = Env("CONSTRUCT_E2E_SEED_USER") is { Length: > 0 } s ? s : user == "root" ? "agent" : user;

        var repo = Path.GetDirectoryName(ProvisionEnvironmentTests.FindRepoFile("Provision-AgentVM.ps1"))!;
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        var zipPath = Path.Combine(Path.GetTempPath(), $"construct-e2e-{commit}.zip");
        Git(repo, "archive", "--format=zip", "--prefix=construct-main/", "-o", zipPath, "HEAD");
        try
        {
            var bytes = await File.ReadAllBytesAsync(zipPath);
            using var app = new TestApp(configureServices: services =>
            {
                services.AddSingleton<ISshRunner>(new SystemSshRunner(new ProcessRunner()));
                services.AddSingleton<IProvisionTargets>(new FixedTarget(target));
            });
            var uri = new Uri("https://github.com/test/repo/" + commit);
            var releases = app.Service<FakeReleaseSource>();
            releases.SourceAssets[commit] = new(commit, "e2e-" + commit, uri, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            releases.Assets[uri] = bytes;
            var client = await app.CreateUserClientAsync("alice");
            await client.CreateVmAsync("e2e");

            var response = await client.PostJsonAsync("/api/v1/vms/e2e/jobs", new
            {
                kind = "provision", requestId = "e2e-" + Guid.NewGuid().ToString("n"), commit,
                desired = new
                {
                    projects = new[] { "default" },
                    settings = new { seedUser = seed, aiTools = "claude-code", vsCodeServer = "false", vsCodeServeWeb = "false", smbShare = "false", t3Code = "false", checkoutProjects = false },
                },
                secrets = Env("CONSTRUCT_E2E_SUDO_PASSWORD") is { Length: > 0 } pw ? new { sudoPassword = pw } : null,
            });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var id = (await response.ReadAsync<VmJobAcceptedResponse>()).JobId;
            JobResponse job;
            var deadline = DateTime.UtcNow.AddHours(2);
            do
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                job = await (await client.GetAsync($"/api/v1/jobs/{id}")).ReadAsync<JobResponse>();
            } while (job.State is JobState.Queued or JobState.Running && DateTime.UtcNow < deadline);

            Assert.True(job.State == JobState.Succeeded, $"{job.Error}\n{string.Join('\n', job.Progress.TakeLast(80).Select(p => p.Text))}");
            var result = ProvisionHarness.Result(job);
            Assert.Equal(commit, result.Applied?.Commit);
            Assert.Equal(result.DesiredHash, result.Applied?.DesiredHash);
            Assert.Equal(result.PayloadHash, result.Applied?.PayloadHash);
        }
        finally { File.Delete(zipPath); }
    }

    private static string Git(string repo, params string[] args)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false, WorkingDirectory = repo };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output;
    }

    private sealed class FixedTarget(SshTarget target) : IProvisionTargets
    {
        public Task<SshTarget?> ResolveAsync(Vm vm, CancellationToken ct) => Task.FromResult<SshTarget?>(target);
        public string? BootstrapPublicKey() => null;
        public Task ForgetHostKeyAsync(SshTarget target, CancellationToken ct) => Task.CompletedTask;
    }
}
