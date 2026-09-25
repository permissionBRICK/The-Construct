using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Microsoft.Data.Sqlite;
namespace Constructd.Tests.Media;

public sealed class ChildMediaReleaseTests
{
    private static async Task<HttpClient> Setup(TestApp app)
    {
        var client = await app.CreateUserClientAsync("alice");
        await app.Vms.AddAsync(new("parent", "alice", 1, 1, 1, app.Clock.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, []), 5, default);
        await app.Service<IUserAllowanceStore>().SetAllowanceAsync("alice", UserAllowance.Unset with { AllowChildCreation = true, MaxRetainedChildren = 5, AllowNeverLifetime = true }, default);
        if (app.Service<ICapacityLedger>() is InMemoryCapacityLedger memory) memory.Mode = CapacityMode.Observe;
        return client;
    }
    private static async Task<string> Upload(HttpClient client, string role, string? dedicatedTo)
    {
        var begin = await client.PostAsJsonAsync("/api/v1/media/uploads", new { name = role + ".iso", role, sizeBytes = 40000, dedicatedTo });
        Assert.Equal(HttpStatusCode.Created, begin.StatusCode);
        var id = (await begin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mediaId").GetString()!;
        var bytes = new byte[40000]; new byte[] { 1, 67, 68, 48, 48, 49, 1 }.CopyTo(bytes, 32768);
        using var chunk = new ByteArrayContent(bytes); chunk.Headers.ContentType = new("application/octet-stream");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync("/api/v1/media/uploads/" + id + "/chunks/0", chunk)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/v1/media/uploads/" + id + "/complete", null)).StatusCode);
        return id;
    }
    private static async Task Create(TestApp app, HttpClient client, string name, string install, string? auxiliary = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/vms/parent/children", new { name, os = "linux", cpus = 1, ramMb = 512, diskGb = 1, lifetime = "10m",
            media = new { installMediaId = install, auxiliaryMediaId = auxiliary }, start = true });
        Assert.Equal(JobState.Succeeded, (await Delegation.LifecycleTests.Finish(app, response)).State);
    }
    private static async Task<JsonElement> Release(HttpClient client, string name, object? body = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/vms/" + name + "/media/release", body ?? new { });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static string Outcome(JsonElement result, string id) =>
        result.GetProperty("media").EnumerateArray().Single(m => m.GetProperty("id").GetString() == id).GetProperty("outcome").GetString()!;

    [Fact]
    public async Task DeleteEjectsRunningChildAndDeletesDedicatedInstallMediaButKeepsAttachedAuxiliary()
    {
        await using var app = new TestApp(); using var client = await Setup(app);
        var install = await Upload(client, "install", "child"); var auxiliary = await Upload(client, "auxiliary", "child");
        await Create(app, client, "child", install, auxiliary);
        Assert.Equal(VmState.Running, await app.Driver.GetStateAsync("child", default));
        var media = app.Service<IMediaStore>(); var path = (await media.GetAsync(install, default))!.Path;

        var result = await Release(client, "child", new { delete = true });
        Assert.True(result.GetProperty("ejected").GetBoolean());
        Assert.Equal("deleted", Outcome(result, install));
        Assert.Single(result.GetProperty("media").EnumerateArray());
        Assert.Contains("media-eject:child:True", app.Service<FakeChildVmDriver>().Calls);
        Assert.Null(await media.GetAsync(install, default));
        Assert.DoesNotContain((await app.Service<IMediaFiles>().ListAsync(default)), f => f.Path == path);
        Assert.DoesNotContain((await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations, r => r.Artifact == path);
        var attached = await app.Service<FakeChildVmDriver>().GetAttachedMediaAsync("child", default);
        Assert.Null(attached.InstallPath); Assert.NotNull(attached.AuxiliaryPath);
        Assert.Equal(auxiliary, Assert.Single(await media.ListReferencesForVmAsync("child", default)).MediaId);

        var again = await Release(client, "child", new { delete = true });
        Assert.False(again.GetProperty("ejected").GetBoolean());
        Assert.Empty(again.GetProperty("media").EnumerateArray());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleaseByDefaultUnbindsTheMediaSoAnotherChildCanUseIt(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "media-release-" + Guid.NewGuid().ToString("n")); Directory.CreateDirectory(root);
        try
        {
            await using var app = sqlite ? TestApp.WithSqlite(Path.Combine(root, "state.db")) : new TestApp(); using var client = await Setup(app);
            var install = await Upload(client, "install", "child");
            await Create(app, client, "child", install);
            Assert.Equal("released", Outcome(await Release(client, "child"), install));
            var item = (await app.Service<IMediaStore>().GetAsync(install, default))!;
            Assert.Null(item.DedicatedTo); Assert.Equal(MediaState.Ready, item.State);
            Assert.Empty(await app.Service<IMediaStore>().ListReferencesAsync(install, default));
            await Create(app, client, "second", install);
            Assert.Equal("second", Assert.Single(await app.Service<IMediaStore>().ListReferencesAsync(install, default)).VmName);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task DeleteKeepsMediaStillUsedByAnotherChild()
    {
        await using var app = new TestApp(); using var client = await Setup(app);
        var install = await Upload(client, "install", null);
        await Create(app, client, "child", install); await Create(app, client, "second", install);
        Assert.Equal("in-use", Outcome(await Release(client, "child", new { delete = true }), install));
        Assert.NotNull(await app.Service<IMediaStore>().GetAsync(install, default));
        Assert.Equal("second", Assert.Single(await app.Service<IMediaStore>().ListReferencesAsync(install, default)).VmName);
        Assert.Equal("deleted", Outcome(await Release(client, "second", new { delete = true }), install));
        Assert.Null(await app.Service<IMediaStore>().GetAsync(install, default));
    }
    [Fact]
    public async Task SharedHostMediaIsOnlyDetached()
    {
        await using var app = new TestApp(); using var client = await Setup(app);
        var media = app.Service<IMediaStore>();
        await media.AddAsync(new("host-install", "host", "host.iso", MediaRole.Install, MediaSource.Url, null, @"C:\media\host.iso", MediaState.Ready,
            40960, 40960, null, null, null, null, null, app.Clock.UtcNow, app.Clock.UtcNow, null) { Shared = true }, default);
        await Create(app, client, "child", "host-install");
        Assert.Equal("released", Outcome(await Release(client, "child", new { delete = true }), "host-install"));
        Assert.NotNull(await media.GetAsync("host-install", default));
        Assert.Empty(await media.ListReferencesForVmAsync("child", default));
    }
    [Fact]
    public async Task StrangersAndPrimaryVmsAreRefused()
    {
        await using var app = new TestApp(); using var client = await Setup(app);
        var install = await Upload(client, "install", "child");
        await Create(app, client, "child", install);
        using var stranger = await app.CreateUserClientAsync("bob");
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.PostAsJsonAsync("/api/v1/vms/child/media/release", new { })).StatusCode);
        var parent = await client.PostAsJsonAsync("/api/v1/vms/parent/media/release", new { });
        Assert.Equal(HttpStatusCode.Conflict, parent.StatusCode); Assert.Contains("not-a-child", await parent.Content.ReadAsStringAsync());
        Assert.NotNull(await app.Service<IMediaStore>().GetAsync(install, default));
        Assert.DoesNotContain(app.Service<FakeChildVmDriver>().Calls, c => c.StartsWith("media-eject:", StringComparison.Ordinal));
    }
}
