using System.Net;
using System.Text;
using System.Text.Json;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;

namespace Constructd.Tests.Provisioning;

/// <summary>A create request with settings: the engine provisions the new VM inside the create job (design 7.3).</summary>
public sealed class CreateWithSettingsTests
{
    /// <summary>The harness, with the cached source as the engine's installed commit (what a create provisions).</summary>
    private static async Task<ProvisionHarness> StartAsync(FakeSshRunner? ssh = null)
    {
        var h = await ProvisionHarness.StartAsync(ssh);
        ((FakeReleaseInfo)h.App.Service<IReleaseInfo>()).Installed = new(ProvisionHarness.Commit, "1.0.0", null, "release");
        return h;
    }

    private static Task<HttpResponseMessage> CreateAsync(ProvisionHarness h, string name, object? desired) =>
        h.Client.PostJsonAsync("/api/v1/vms", new { name, cpu = 2, ramGb = 4, diskGb = 32, desired });

    [Fact]
    public async Task The_create_job_provisions_the_vm_and_hands_it_its_token()
    {
        await using var h = await StartAsync();
        var response = await CreateAsync(h, "fresh", ProvisionHarness.Desired("web"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = await h.Client.WaitForJobAsync((await response.ReadAsync<JobAcceptedResponse>()).JobId);

        Assert.True(JobState.Succeeded == job.State, job.Error);
        var json = JsonSerializer.SerializeToElement(job.Result);
        Assert.Equal("fresh", json.GetProperty("name").GetString());
        var token = json.GetProperty("vmToken").GetString()!;
        Assert.True(json.GetProperty("provision").GetProperty("succeeded").GetBoolean());
        Assert.Contains(job.Progress, p => p.Step == "provision" && p.State == "succeeded");

        var provision = h.Ssh.Calls.Single(c => c.Command.Contains("bin/provision.sh", StringComparison.Ordinal));
        Assert.Equal("fresh", FakeSshRunner.Variable(provision.Command, "CONSTRUCT_INSTANCE_NAME"));
        Assert.Equal("web", FakeSshRunner.Variable(provision.Command, "PROJECTS"));
        var stage = h.Ssh.Calls.Single(c => c.Command.StartsWith("umask 077; cat > /tmp/.construct-provision-secrets.", StringComparison.Ordinal));
        Assert.Contains($"CONSTRUCT_VM_TOKEN_B64='{Convert.ToBase64String(Encoding.UTF8.GetBytes(token))}'", stage.StandardInput, StringComparison.Ordinal);
        Assert.DoesNotContain(token, string.Join('\n', job.Progress.Select(p => p.Text)), StringComparison.Ordinal);

        // The settings are the VM's now and nothing waits.
        var desired = await (await h.Client.GetAsync("/api/v1/vms/fresh/desired")).ReadAsync<VmDesiredStateResponse>();
        Assert.Equal(["web"], desired.Desired!.Projects);
        var status = await (await h.Client.GetAsync("/api/v1/vms/fresh/provision-status")).ReadAsync<ProvisionStatusResponse>();
        Assert.False(status.SettingsChanged);
    }

    [Fact]
    public async Task The_create_request_secrets_reach_the_first_provision_and_end_with_the_job()
    {
        await using var h = await StartAsync();
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes("https://bob:create-secret-8642@git.example.test"));
        var response = await h.Client.PostJsonAsync("/api/v1/vms", new
        {
            name = "fresh", cpu = 2, ramGb = 4, diskGb = 32, desired = ProvisionHarness.Desired("web"), secrets = new { gitCloneCredentialsB64 = creds },
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = await h.Client.WaitForJobAsync((await response.ReadAsync<JobAcceptedResponse>()).JobId);
        Assert.True(JobState.Succeeded == job.State, job.Error);

        var stage = h.Ssh.Calls.Single(c => c.Command.StartsWith("umask 077; cat > /tmp/.construct-provision-secrets.", StringComparison.Ordinal));
        Assert.Contains($"GIT_CLONE_CREDENTIALS_B64='{creds}'", stage.StandardInput, StringComparison.Ordinal);
        Assert.Contains("CONSTRUCT_VM_TOKEN_B64=", stage.StandardInput, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Ssh.Calls, c => c.Command.Contains(creds, StringComparison.Ordinal));
        Assert.DoesNotContain("create-secret-8642", JsonSerializer.Serialize(job), StringComparison.Ordinal);

        // A later provision of that VM without secrets has none: nothing was kept.
        h.Ssh.Calls.Clear();
        var again = await h.Client.WaitForJobAsync((await (await h.Client.PostJsonAsync("/api/v1/vms/fresh/jobs",
            new { kind = "provision", requestId = "later", commit = ProvisionHarness.Commit })).ReadAsync<VmJobAcceptedResponse>()).JobId);
        Assert.Equal(JobState.Succeeded, again.State);
        Assert.DoesNotContain(h.Ssh.Calls, c => c.StandardInput?.Contains("GIT_CLONE_CREDENTIALS_B64", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("vmTokenB64", "dG9rZW4=")]
    [InlineData("backupKey", "AGE-SECRET-KEY-1QQQQ")]
    [InlineData("gitCloneCredentialsB64", "not base64!")]
    public async Task Create_secrets_the_job_does_not_take_or_cannot_read_refuse_the_create(string field, string value)
    {
        await using var h = await StartAsync();
        var response = await h.Client.PostJsonAsync("/api/v1/vms", new
        {
            name = "bad", cpu = 2, ramGb = 4, diskGb = 32, desired = ProvisionHarness.Desired(), secrets = new Dictionary<string, string> { [field] = value },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("secrets." + field, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null(await h.App.Vms.GetAsync("bad", default));
    }

    [Fact]
    public async Task A_failed_first_provision_fails_the_create_job_and_keeps_the_vm()
    {
        var ssh = new FakeSshRunner { Provision = _ => (1, FakeSshRunner.Clean) };
        await using var h = await StartAsync(ssh);
        h.Ssh.Calls.Clear();
        var job = await h.Client.WaitForJobAsync((await (await CreateAsync(h, "fresh", ProvisionHarness.Desired())).ReadAsync<JobAcceptedResponse>()).JobId);

        Assert.Equal(JobState.Failed, job.State);
        Assert.StartsWith("provision-critical-failure", job.Error, StringComparison.Ordinal);
        var json = JsonSerializer.SerializeToElement(job.Result);
        Assert.Equal("fresh", json.GetProperty("name").GetString());
        Assert.False(json.GetProperty("provision").GetProperty("succeeded").GetBoolean());
        Assert.Equal("provision", json.GetProperty("provision").GetProperty("failedStep").GetString());
        Assert.NotNull(await h.App.Vms.GetAsync("fresh", default));
        Assert.NotNull((await (await h.Client.GetAsync("/api/v1/vms/fresh/desired")).ReadAsync<VmDesiredStateResponse>()).Desired);

        // A provision job with the stored settings runs it again.
        ssh.Provision = _ => (0, FakeSshRunner.Clean);
        var again = await h.Client.WaitForJobAsync((await (await h.Client.PostJsonAsync("/api/v1/vms/fresh/jobs",
            new { kind = "provision", requestId = "again", commit = ProvisionHarness.Commit })).ReadAsync<VmJobAcceptedResponse>()).JobId);
        Assert.Equal(JobState.Succeeded, again.State);
    }

    [Fact]
    public async Task A_create_without_settings_only_builds_the_vm_and_its_result_is_unchanged()
    {
        await using var h = await StartAsync();
        var job = await h.Client.WaitForJobAsync((await (await CreateAsync(h, "plain", null)).ReadAsync<JobAcceptedResponse>()).JobId);

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Empty(h.Ssh.Calls);
        var keys = JsonSerializer.SerializeToElement(job.Result).EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["name", "endpoint", "vmToken"], keys);
        Assert.Null((await (await h.Client.GetAsync("/api/v1/vms/plain/desired")).ReadAsync<VmDesiredStateResponse>()).Desired);
    }

    [Fact]
    public async Task Without_a_known_source_the_create_is_refused_before_anything_is_built()
    {
        await using var h = await ProvisionHarness.StartAsync();
        ((FakeReleaseInfo)h.App.Service<IReleaseInfo>()).Installed = new("unknown", "1.0.0", null, "release");
        var response = await CreateAsync(h, "fresh", ProvisionHarness.Desired());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("source-commit-unknown", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null(await h.App.Vms.GetAsync("fresh", default));
    }

    [Fact]
    public async Task Invalid_settings_refuse_the_create_before_anything_is_built()
    {
        await using var h = await StartAsync();
        var response = await CreateAsync(h, "bad", new { projects = new[] { "a b" }, settings = new { } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await h.App.Vms.GetAsync("bad", default));
    }
}
