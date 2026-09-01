using System.Net;
using Constructd.Api.Contracts;
using Constructd.Core.Domain;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

/// <summary>
/// Quota and forward caps are limits, not suggestions: they are enforced inside the store/manager, so
/// simultaneous requests cannot slip through the gap between "count" and "insert".
/// </summary>
public class ConcurrencyTests
{
    [Fact]
    public async Task Simultaneous_creates_cannot_exceed_the_quota()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob", maxVms: 2);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            bob.PostJsonAsync("/api/v1/vms", new { name = $"vm-{i}", cpu = 2, ramGb = 4, diskGb = 32 })));

        var accepted = attempts.Count(r => r.StatusCode == HttpStatusCode.Accepted);
        var refused = attempts.Count(r => r.StatusCode == HttpStatusCode.Forbidden);

        Assert.Equal(2, accepted);
        Assert.Equal(6, refused);

        // And the registry agrees.
        foreach (var response in attempts)
        {
            response.Dispose();
        }

        Assert.Equal(2, await app.Vms.CountByOwnerAsync("bob", CancellationToken.None));
    }

    [Fact]
    public async Task Simultaneous_creates_of_the_same_name_produce_exactly_one_vm()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob", maxVms: 10);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            bob.PostJsonAsync("/api/v1/vms", new { name = "work-vm", cpu = 2, ramGb = 4, diskGb = 32 })));

        Assert.Equal(1, attempts.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        Assert.Equal(5, attempts.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        foreach (var response in attempts)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Simultaneous_forward_requests_cannot_exceed_the_cap()
    {
        // The guest holds a VM token and can fire requests as fast as it likes.
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        var job = await bob.CreateVmAsync("work-vm");
        using var guest = app.CreateVmTokenClient(job.VmToken());

        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            guest.PostJsonAsync("/api/v1/vms/work-vm/forwards",
                new { vmPort = 3000 + i, label = $"p{i}", target = "client" })));

        // MaxForwardsPerVm is 3 in the test host.
        Assert.Equal(3, attempts.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(7, attempts.Count(r => r.StatusCode == HttpStatusCode.Forbidden));

        foreach (var response in attempts)
        {
            response.Dispose();
        }

        var listed = await (await guest.GetAsync("/api/v1/vms/work-vm/forwards"))
            .ReadAsync<List<ForwardResponse>>();
        Assert.Equal(3, listed.Count);
    }

    [Fact]
    public async Task Simultaneous_host_forwards_never_share_a_public_port()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        await bob.CreateVmAsync("work-vm");

        var attempts = await Task.WhenAll(Enumerable.Range(0, 3).Select(i =>
            bob.PostJsonAsync("/api/v1/vms/work-vm/forwards",
                new { vmPort = 4000 + i, label = $"p{i}", target = "host" })));

        var ports = new List<int>();
        foreach (var response in attempts)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var forward = await response.ReadAsync<ForwardResponse>();
            Assert.Equal(ForwardTarget.Host, forward.Target);
            ports.Add(forward.PublicPort!.Value);
            response.Dispose();
        }

        Assert.Equal(3, ports.Distinct().Count());
    }
}
