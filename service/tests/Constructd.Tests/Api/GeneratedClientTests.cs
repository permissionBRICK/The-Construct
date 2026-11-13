using System.Net;
using Constructd.Client;
using Constructd.Tests.Support;
using Role = Constructd.Core.Domain.Role;

namespace Constructd.Tests.Api;

/// <summary>
/// Smoke test of the C# client generated from the committed contract (src/Constructd.Client) against
/// the in-process host: typed calls work end to end, and errors arrive as the Problem shape.
/// </summary>
public class GeneratedClientTests
{
    [Fact]
    public async Task Generated_client_drives_the_host()
    {
        using var app = new TestApp();
        var http = await app.CreateUserClientAsync("alice", Role.Admin);
        var client = new ConstructdClient(http);

        var me = await client.WhoAmIAsync();
        Assert.Equal("alice", me.Name);
        Assert.True(me.Known);

        var health = await client.HealthAsync();
        Assert.Equal("ok", health.Status);
        Assert.NotEmpty(health.ApiFeatures);

        var capabilities = await client.HostCapabilitiesAsync();
        Assert.Equal("fake", capabilities.Backend);

        var accepted = await client.CreateVmAsync(new CreateVmRequest { Name = "smoke", Cpu = 2, RamGb = 4, DiskGb = 32 });
        var job = await client.GetJobAsync(accepted.JobId);
        for (var i = 0; i < 500 && job.State is JobState.Queued or JobState.Running; i++)
        {
            await Task.Delay(20);
            job = await client.GetJobAsync(accepted.JobId);
        }

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Contains(await client.ListVmsAsync(), vm => vm.Name == "smoke");
        Assert.Equal(VmState.Running, (await client.GetVmStateAsync("smoke")).State);
        Assert.Equal(2, (await client.GetVmCpuAsync("smoke")).CurrentCpus);

        // A repeated create with the same operation key is a success (200, replayed), not an error.
        http.DefaultRequestHeaders.Add("X-Construct-Operation-Key", "smoke-key-0001");
        var keyed = await client.CreateVmAsync(new CreateVmRequest { Name = "smoke2", Cpu = 1, RamGb = 2, DiskGb = 16 });
        var again = await client.CreateVmAsync(new CreateVmRequest { Name = "smoke2", Cpu = 1, RamGb = 2, DiskGb = 16 });
        http.DefaultRequestHeaders.Remove("X-Construct-Operation-Key");
        Assert.Null(keyed.Replayed);
        Assert.True(again.Replayed);
        Assert.Equal(keyed.JobId, again.JobId);

        // An update that names one field leaves the others alone, the nested override included.
        await client.CreateUserAsync(new CreateUserRequest { Name = "bob", Role = "user", MaxVms = 1, AllowNested = true });
        var updated = await client.UpdateUserAsync("bob", new UserUpdateRequest { MaxVms = 2 });
        Assert.True(updated.AllowNested);
        Assert.True((await client.UpdateUserAsync("bob", new UserUpdateRequest { AllowNested = false })).AllowNested == false);

        // A delete answers 202 with its job.
        var deleted = await client.DeleteVmAsync("smoke");
        Assert.False(string.IsNullOrEmpty(deleted.JobId));

        var token = await client.IssueUserTokenAsync("alice", new CreateTokenRequest { Label = "smoke" });
        Assert.False(string.IsNullOrEmpty(token.Token));

        var missing = await Assert.ThrowsAsync<ApiException<Problem>>(() => client.GetVmAsync("missing"));
        Assert.Equal((int)HttpStatusCode.NotFound, missing.StatusCode);
        Assert.False(string.IsNullOrEmpty(missing.Result.Detail));
    }
}
