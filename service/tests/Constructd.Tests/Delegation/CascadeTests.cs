using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;
namespace Constructd.Tests.Delegation;

public sealed class CascadeTests
{
    private static async Task<string> Preview(HttpClient client)
    {
        var response = await client.DeleteAsync("/api/v1/vms/parent");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cascade-confirmation-required", json.GetProperty("code").GetString());
        return json.GetProperty("cascadeToken").GetString()!;
    }
    private static Task<HttpResponseMessage> Confirm(HttpClient client, string token) => client.SendAsync(new(HttpMethod.Delete, "/api/v1/vms/parent")
        { Content = JsonContent.Create(new { cascade = new { token } }) });
    [Fact]
    public async Task ScopeChangedBetweenConfirmationAndExecutionCannotDeleteSilently()
    {
        await using var app = new TestApp(); using var owner = await LifecycleTests.Setup(app);
        var token = await Preview(owner);
        await LifecycleTests.Finish(app, await owner.PostAsJsonAsync("/api/v1/vms/parent/children", LifecycleTests.Request("late-child")));
        var refused = await Confirm(owner, token); Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("cascade-scope-changed", await refused.Content.ReadAsStringAsync());
        Assert.False((await app.Vms.GetAsync("parent", default))!.Deleting);
        Assert.DoesNotContain(app.Driver.Calls, c => c.StartsWith("remove:", StringComparison.Ordinal));
        Assert.Equal(2, (await app.Service<IVmDelegationRepository>().ListChildrenAsync("parent", default)).Count);
        var fresh = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cascadeToken").GetString()!;
        Assert.Equal(JobState.Succeeded, (await LifecycleTests.Finish(app, await Confirm(owner, fresh))).State);
        Assert.Null(await app.Vms.GetAsync("parent", default)); Assert.Empty(await app.Service<IVmDelegationRepository>().ListChildrenAsync("parent", default));
    }
    [Fact]
    public async Task PartialCleanupRevokesDelegationKeepsOwnershipAndLiabilityThenRetries()
    {
        await using var app = new TestApp(); using var owner = await LifecycleTests.Setup(app); using var bob = await app.CreateUserClientAsync("bob");
        await LifecycleTests.Finish(app, await owner.PostAsJsonAsync("/api/v1/vms/parent/children", LifecycleTests.Request("private-child")));
        (await owner.PutAsJsonAsync("/api/v1/vms/child/sharing", new { scope = "host" })).EnsureSuccessStatusCode();
        using var primary = app.CreateVmTokenClient(await app.Service<IVmTokenIssuer>().IssueVmTokenAsync("parent", VmTokenKind.Primary, default));
        var session = app.Service<IConsoleSessionStore>().TryCreate("child", "bob", 100, 100, TimeSpan.FromMinutes(5), app.Clock.UtcNow)!;
        var token = await Preview(owner);
        app.Service<FakeChildVmDriver>().RemoveFailure = new InvalidOperationException("private dependency detail");
        var failed = await LifecycleTests.Finish(app, await Confirm(owner, token));
        Assert.Equal(JobState.Failed, failed.State);
        Assert.DoesNotContain("private dependency detail", JsonSerializer.Serialize(failed));
        Assert.True((await app.Vms.GetAsync("parent", default))!.ChildCreationClosed);
        Assert.Null((await app.Vms.GetAsync("parent", default))!.VmTokenHash);
        Assert.Equal(2, (await app.Service<IVmDelegationRepository>().ListChildrenAsync("parent", default)).Count);
        Assert.NotEmpty((await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations);
        Assert.Null(app.Service<IConsoleSessionStore>().Get(session.Id, app.Clock.UtcNow));
        Assert.Equal(HttpStatusCode.Unauthorized, (await primary.GetAsync("/api/v1/vms/parent/identity")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostAsJsonAsync("/api/v1/vms/child/lifecycle", new { action = "save" })).StatusCode);
        Assert.False((await owner.PostAsJsonAsync("/api/v1/vms/parent/children", LifecycleTests.Request("blocked"))).IsSuccessStatusCode);
        app.Service<FakeChildVmDriver>().RemoveFailure = null;
        Assert.Equal(JobState.Succeeded, (await LifecycleTests.Finish(app, await Confirm(owner, await Preview(owner)))).State);
        Assert.Null(await app.Vms.GetAsync("parent", default)); Assert.Empty((await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations);
    }
    [Fact]
    public async Task RepeatedDeleteDuringCleanupReturnsSameJobAndDoesNotQueueAnother()
    {
        await using var app = new TestApp(); using var owner = await LifecycleTests.Setup(app);
        app.Forwards.HoldRemoveAll = true;
        var accepted = await Confirm(owner, await Preview(owner));
        var id = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;
        try
        {
            await app.Forwards.RemoveAllStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var retry = await owner.DeleteAsync("/api/v1/vms/parent");
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.Equal(id, (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString());
        }
        finally { app.Forwards.ReleaseRemoveAll(); }
        Assert.Equal(JobState.Succeeded, (await LifecycleTests.Finish(app, id)).State);
    }
}
