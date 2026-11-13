using System.Net;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;

namespace Constructd.Tests.Provisioning;

/// <summary><c>POST /jobs/{id}/retry</c> (design 7.1) for a provision job; reinstall and restore are in <see cref="ReinstallJobTests"/>.</summary>
public sealed class JobRetryTests
{
    /// <summary>The harness with the cached source as the engine's installed commit, which a retry provisions.</summary>
    internal static async Task<ProvisionHarness> StartAsync(FakeSshRunner? ssh = null)
    {
        var h = await ProvisionHarness.StartAsync(ssh);
        Installed(h);
        return h;
    }

    internal static void Installed(ProvisionHarness h) =>
        ((FakeReleaseInfo)h.App.Service<IReleaseInfo>()).Installed = new(ProvisionHarness.Commit, "1.0.0", null, "release");

    [Fact]
    public async Task A_failed_provision_runs_again_with_the_stored_settings()
    {
        var ssh = new FakeSshRunner { Provision = _ => (1, FakeSshRunner.Clean) };
        await using var h = await StartAsync(ssh);
        var failed = await h.RunAsync("click-1", ProvisionHarness.Desired("web"));
        Assert.Equal(JobState.Failed, failed.State);

        ssh.Provision = _ => (0, FakeSshRunner.Clean);
        var response = await h.Client.PostAsync($"/api/v1/jobs/{failed.Id}/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.ReadAsync<VmJobAcceptedResponse>();
        Assert.Equal(JobKinds.Provision, accepted.Kind);
        Assert.False(accepted.Replayed);
        Assert.Equal(JobState.Succeeded, (await h.Client.WaitForJobAsync(accepted.JobId)).State);
        Assert.All(h.Ssh.Commands.Where(c => c.Contains("bin/provision.sh", StringComparison.Ordinal)),
            c => Assert.Equal("web", FakeSshRunner.Variable(c, "PROJECTS")));

        // Once only: a repeat answers the same retry, and a succeeded job is not retried.
        Assert.Equal(accepted.JobId, (await (await h.Client.PostAsync($"/api/v1/jobs/{failed.Id}/retry", null)).ReadAsync<VmJobAcceptedResponse>()).JobId);
        var done = await h.Client.PostAsync($"/api/v1/jobs/{accepted.JobId}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, done.StatusCode);
        Assert.Contains("job-not-failed", await done.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_latest_job_of_the_vm_and_only_its_owner_can_retry()
    {
        var ssh = new FakeSshRunner { Provision = _ => (1, FakeSshRunner.Clean) };
        await using var h = await StartAsync(ssh);
        var older = await h.RunAsync("click-1");
        h.App.Clock.Advance(TimeSpan.FromMinutes(1));
        var newer = await h.RunAsync("click-2");
        Assert.Equal(JobState.Failed, newer.State);

        var superseded = await h.Client.PostAsync($"/api/v1/jobs/{older.Id}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, superseded.StatusCode);
        Assert.Contains("job-superseded", await superseded.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var bob = await h.App.CreateUserClientAsync("bob");
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostAsync($"/api/v1/jobs/{newer.Id}/retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.App.CreateVmTokenClient(h.VmToken!).PostAsync($"/api/v1/jobs/{newer.Id}/retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.PostAsync("/api/v1/jobs/nope/retry", null)).StatusCode);
    }

    [Fact]
    public async Task Other_job_kinds_cannot_be_retried()
    {
        await using var h = await StartAsync();
        var create = (await h.App.Service<Constructd.Core.Abstractions.IJobQueryStore>().ListAsync(default)).Single(j => j.Kind == JobKinds.CreateVm);
        var response = await h.Client.PostAsync($"/api/v1/jobs/{create.Id}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("not-retryable", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_job_of_an_earlier_vm_under_the_name_is_not_retried_on_the_new_one()
    {
        var ssh = new FakeSshRunner { Provision = _ => (1, FakeSshRunner.Clean) };
        await using var h = await StartAsync(ssh);
        var failed = await h.RunAsync("click-1");
        var removal = await h.Client.DeleteAsync($"/api/v1/vms/{ProvisionHarness.Vm}");
        Assert.Equal(HttpStatusCode.Accepted, removal.StatusCode);
        Assert.Equal(JobState.Succeeded, (await h.Client.WaitForJobAsync((await removal.ReadAsync<JobAcceptedResponse>()).JobId)).State);
        h.App.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Client.CreateVmAsync(ProvisionHarness.Vm);

        var response = await h.Client.PostAsync($"/api/v1/jobs/{failed.Id}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("vm-changed", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
