using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Tests.Support;
namespace Constructd.Tests.Delegation;

public sealed class JobAccessTests
{
    [Fact]
    public async Task SharedCallerAndPrimarySeeAndCancelOnlyTheirInitiatedJobs()
    {
        await using var app = new TestApp(); using var alice = await app.CreateUserClientAsync("alice"); using var bob = await app.CreateUserClientAsync("bob");
        await app.Vms.AddAsync(new("primary", "bob", 1, 1, 1, app.Clock.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, []), 5, default);
        using var primary = app.CreateVmTokenClient(await app.Service<IVmTokenIssuer>().IssueVmTokenAsync("primary", VmTokenKind.Primary, default));
        var store = app.Service<IJobStore>();
        foreach (var (id, initiator) in new[] { ("own", "alice"), ("shared", "bob"), ("primary", "vm:primary") })
        {
            var job = new Job(id, "vm-shutdown", "shared-child", "alice", JobState.Queued, [], null, null, app.Clock.UtcNow, null, initiator);
            await store.UpsertAsync(job, default);
            await app.Service<IPersistedJobRunner>().StartPersistedAsync(job, new CancellationTokenSource(), async (_, token) =>
                { await Task.Delay(Timeout.Infinite, token); return new JobOutcome(new { }); }, default);
        }
        async Task<string[]> List(HttpClient client) => (await client.GetFromJsonAsync<JsonElement>("/api/v1/jobs")).EnumerateArray().Select(j => j.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(["shared"], await List(bob)); Assert.Equal(["primary"], await List(primary)); Assert.Equal(3, (await List(alice)).Length);
        foreach (var (caller, allowed, refused) in new[] { (bob, "shared", "primary"), (primary, "primary", "shared") })
        {
            Assert.Equal(HttpStatusCode.OK, (await caller.PostAsJsonAsync("/api/v1/jobs/" + allowed + "/cancel", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await caller.PostAsJsonAsync("/api/v1/jobs/" + refused + "/cancel", new { })).StatusCode);
        }
        foreach (var id in new[] { "own", "shared", "primary" })
        {
            await alice.PostAsJsonAsync("/api/v1/jobs/" + id + "/cancel", new { });
            Assert.Equal(JobState.Cancelled, (await LifecycleTests.Finish(app, id)).State);
        }
    }
    [Theory]
    [InlineData("kind=vm-shutdown", "first")]
    [InlineData("state=failed", "second")]
    [InlineData("vm=CHILD", "first")]
    [InlineData("since=2026-09-02T00:00:00Z", "second")]
    [InlineData("limit=1", "second")]
    public async Task EveryJobFilterNarrowsResults(string query, string expected)
    {
        await using var app = new TestApp(); using var client = await app.CreateUserClientAsync("alice");
        var store = app.Service<IJobStore>();
        await store.UpsertAsync(new("first", "vm-shutdown", "child", "alice", JobState.Succeeded, [], null, null, DateTimeOffset.Parse("2026-09-01T00:00:00Z"), null), default);
        await store.UpsertAsync(new("second", "vm-restart", "other", "alice", JobState.Failed, [], null, null, DateTimeOffset.Parse("2026-09-03T00:00:00Z"), null), default);
        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/jobs?" + query);
        Assert.Equal(expected, Assert.Single(json.EnumerateArray()).GetProperty("id").GetString());
    }
    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    [InlineData("limit=bad")]
    [InlineData("since=bad")]
    public async Task InvalidJobFiltersAreBadRequests(string query)
    {
        await using var app = new TestApp(); using var client = await app.CreateUserClientAsync("alice");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/jobs?" + query)).StatusCode);
    }
}
