using System.Net;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

/// <summary>
/// The VM-scoped token is a one-time secret: it is handed to the first reader of the creation job
/// and to nobody after that — not to a second GET, not to an SSE reconnect, and not to anything that
/// reads the stored job.
/// </summary>
public class JobSecretTests
{
    [Fact]
    public async Task The_vm_token_is_handed_out_once_and_never_again()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");

        var jobId = await bob.StartCreateVmAsync("work-vm");
        var first = await bob.WaitForJobAsync(jobId);

        var token = first.VmToken();

        // Later retrievals keep the durable part of the result and drop only the token.
        var second = await (await bob.GetAsync($"/api/v1/jobs/{jobId}")).ReadAsync<JobResponse>();
        Assert.Null(second.VmTokenOrNull());
        Assert.Equal("work-vm", second.ResultElement("name").GetString());
        Assert.Equal(2201, second.ResultElement("endpoint").GetProperty("sshPort").GetInt32());

        var third = await (await bob.GetAsync($"/api/v1/jobs/{jobId}")).ReadAsync<JobResponse>();
        Assert.Null(third.VmTokenOrNull());

        // The token that was handed out is the real one.
        using var guest = app.CreateVmTokenClient(token);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync("/api/v1/vms/work-vm/forwards")).StatusCode);
    }

    [Fact]
    public async Task A_replayed_event_stream_does_not_repeat_the_secret()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");

        // Subscribe directly, without a GET first: the stream itself is the first retrieval.
        var jobId = await bob.StartCreateVmAsync("work-vm");

        // First subscriber consumes the token...
        var firstStream = await (await bob.GetAsync($"/api/v1/jobs/{jobId}/events")).Content.ReadAsStringAsync();
        Assert.Matches("\"vmToken\":\"[A-Za-z0-9_-]+\"", firstStream);

        // ...every later subscriber replays the same log without it, but with the rest of the result.
        var secondStream = await (await bob.GetAsync($"/api/v1/jobs/{jobId}/events")).Content.ReadAsStringAsync();
        Assert.Contains("event: state", secondStream);
        Assert.Contains("\"vmToken\":null", secondStream);
        Assert.Contains("\"name\":\"work-vm\"", secondStream);

        var job = await (await bob.GetAsync($"/api/v1/jobs/{jobId}")).ReadAsync<JobResponse>();
        Assert.Null(job.VmTokenOrNull());
    }

    [Fact]
    public async Task The_stored_job_never_contains_the_secret()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");

        var jobId = await bob.StartCreateVmAsync("work-vm");
        var job = await bob.WaitForJobAsync(jobId);
        var token = job.VmToken();

        // What the store holds — i.e. what a restart or a durable backend would ever see.
        var stored = await app.Service<IJobStore>().GetAsync(jobId, CancellationToken.None);

        Assert.NotNull(stored);
        var serialized = System.Text.Json.JsonSerializer.Serialize(stored);
        Assert.DoesNotContain(token, serialized, StringComparison.Ordinal);
        Assert.Contains("work-vm", serialized, StringComparison.Ordinal);

        // ...and the token is gone from every later projection of the same job.
        var reread = await (await bob.GetAsync($"/api/v1/jobs/{jobId}")).ReadAsync<JobResponse>();
        Assert.DoesNotContain(
            token,
            System.Text.Json.JsonSerializer.Serialize(reread.Result),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_job_hands_out_no_secret()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        app.Driver.Reachable = false;

        var job = await bob.WaitForJobAsync(await bob.StartCreateVmAsync("work-vm"));

        Assert.Equal(JobState.Failed, job.State);
        Assert.Null(job.Result);
    }
}
