using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Source;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Tests.Support;
namespace Constructd.Tests.Source;

public sealed class SourceEndpointsTests
{
    internal static async Task Seed(TestApp app)
    {
        await app.Vms.AddAsync(new("vm", "alice", 1, 1, 8, app.Clock.UtcNow, VmState.Running, null, TokenHasher.Hash("vm-secret"), IdlePolicy.Disabled, []), 5, default);
        await app.Vms.AddAsync(new("other", "alice", 1, 1, 8, app.Clock.UtcNow, VmState.Running, null, TokenHasher.Hash("other-secret"), IdlePolicy.Disabled, []), 5, default);
    }
    internal static byte[] Add(TestApp app)
    {
        var bytes = SourceFixture.Zip(); var source = app.Service<FakeReleaseSource>();
        var uri = new Uri("https://github.com/test/source.zip"); source.Assets[uri] = bytes;
        source.SourceAssets[SourceFixture.Commit] = new(SourceFixture.Commit, "host-" + SourceFixture.Commit, uri, bytes.Length,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))); return bytes;
    }
    internal static async Task<JsonElement> Ensure(HttpClient client, string key, string vm = "vm", string commit = SourceFixture.Commit)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/vms/{vm}/source") { Content = JsonContent.Create(new { commit }) };
        req.Headers.Add("X-Construct-Operation-Key", key); var response = await client.SendAsync(req);
        response.EnsureSuccessStatusCode(); var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (response.StatusCode == HttpStatusCode.Accepted) Assert.Equal("/api/v1/jobs/" + body.GetProperty("jobId").GetString(), response.Headers.Location!.ToString());
        return body;
    }
    internal static async Task<Job> Wait(TestApp app, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var engine = app.Service<IJobEngine>();
        await foreach (var unused in engine.SubscribeAsync(id, timeout.Token)) { }
        return (await engine.GetAsync(id, default))!;
    }
    [Fact]
    public async Task QueueReadyReplayFetchPinDeleteAndReensure()
    {
        await using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin); await Seed(app); var bytes = Add(app);
        var queued = await Ensure(owner, "source-first"); Assert.Equal("downloading", queued.GetProperty("state").GetString());
        var jobId = queued.GetProperty("jobId").GetString()!; Assert.Equal(JobState.Succeeded, (await Wait(app, jobId)).State);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/jobs/" + jobId)).StatusCode);
        var listed = await owner.GetFromJsonAsync<JsonElement>("/api/v1/jobs?vm=vm");
        Assert.Contains(listed.EnumerateArray(), j => j.GetProperty("id").GetString() == jobId && j.GetProperty("kind").GetString() == "source-fetch");
        var replay = await Ensure(owner, "source-first"); Assert.Equal(jobId, replay.GetProperty("jobId").GetString()); Assert.True(replay.GetProperty("replayed").GetBoolean());
        var ready = await Ensure(owner, "source-second"); Assert.Equal("ready", ready.GetProperty("state").GetString());
        Assert.Equal(bytes.Length, ready.GetProperty("sizeBytes").GetInt64());
        var replayReady = await Ensure(owner, "source-second"); Assert.Equal(ready.GetProperty("sha256").GetString(), replayReady.GetProperty("sha256").GetString());
        var vm = await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/vm"); Assert.Equal(SourceFixture.Commit, vm.GetProperty("sourceCommit").GetString());
        using var guest = app.CreateVmTokenClient("vm-secret");
        var fetched = await guest.GetAsync("/api/v1/vms/vm/source/" + SourceFixture.Commit);
        Assert.Equal(bytes, await fetched.Content.ReadAsByteArrayAsync()); Assert.Equal("application/zip", fetched.Content.Headers.ContentType!.MediaType);
        Assert.Equal(bytes.Length, fetched.Content.Headers.ContentLength);
        Assert.Equal("\"" + ready.GetProperty("sha256").GetString() + "\"", fetched.Headers.ETag!.Tag);
        Assert.Equal(SourceFixture.Commit, Assert.Single(fetched.Headers.GetValues("X-Construct-Source-Commit")));
        Assert.Equal(ready.GetProperty("sha256").GetString(), Assert.Single(fetched.Headers.GetValues("X-Construct-Source-Sha256")));
        Assert.Equal("attachment", fetched.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("construct-source-" + SourceFixture.Commit + ".zip", fetched.Content.Headers.ContentDisposition.FileName);
        var listing = await admin.GetFromJsonAsync<JsonElement>("/api/v1/host/source-cache");
        Assert.Equal(bytes.Length, listing.GetProperty("committedBytes").GetInt64());
        Assert.Equal("vm", listing.GetProperty("items")[0].GetProperty("pinnedBy")[0].GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync("/api/v1/host/source-cache/" + SourceFixture.Commit)).StatusCode);
        var deleted = await admin.DeleteAsync("/api/v1/host/source-cache/" + SourceFixture.Commit + "?force=true");
        Assert.Equal(HttpStatusCode.Accepted, deleted.StatusCode); await Wait(app, (await deleted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!);
        var again = await Ensure(owner, "source-third"); await Wait(app, again.GetProperty("jobId").GetString()!);
        Assert.Equal(2, app.Service<FakeReleaseSource>().DownloadCount);
        foreach (var (target, commit) in new[] { ("other", SourceFixture.Commit), ("vm", SourceFixture.Other) })
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/vms/{target}/source") { Content = JsonContent.Create(new { commit }) };
            req.Headers.Add("X-Construct-Operation-Key", "source-first"); Assert.Equal(HttpStatusCode.Conflict, (await owner.SendAsync(req)).StatusCode);
        }
    }
    [Theory]
    [InlineData("owner", 200, 202, 403)]
    [InlineData("admin", 200, 202, 200)]
    [InlineData("foreign", 403, 403, 403)]
    [InlineData("self", 200, 403, 403)]
    [InlineData("other", 403, 403, 403)]
    [InlineData("anonymous", 401, 401, 401)]
    public async Task AuthMatrix(string actor, int fetch, int ensure, int list)
    {
        await using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice"); await Seed(app); Add(app);
        await app.Service<ISourceCache>().EnsureAsync(SourceFixture.Commit, null, default);
        using var client = actor switch { "owner" => await app.CreateTokenClientAsync("alice"), "admin" => await app.CreateUserClientAsync("admin", Role.Admin),
            "foreign" => await app.CreateUserClientAsync("bob"), "self" => app.CreateVmTokenClient("vm-secret"), "other" => app.CreateVmTokenClient("other-secret"), _ => app.CreateAnonymousClient() };
        Assert.Equal(fetch, (int)(await client.GetAsync("/api/v1/vms/vm/source/" + SourceFixture.Commit)).StatusCode);
        // Queued using a second commit so a successful ensure returns 202, independently of worker outcome.
        Assert.Equal(ensure, (int)(await client.PostAsJsonAsync("/api/v1/vms/vm/source", new { commit = SourceFixture.Other })).StatusCode);
        Assert.Equal(list, (int)(await client.GetAsync("/api/v1/host/source-cache")).StatusCode);
        if (actor is not ("owner" or "admin")) Assert.Equal(403 == list ? 403 : 401, (int)(await client.PostAsJsonAsync("/api/v1/host/source-cache/cleanup", new { })).StatusCode);
    }
    [Theory]
    [InlineData(false, "not-a-primary")]
    [InlineData(true, "vm-deleting")]
    public async Task EnsureAndFetchRefuseIneligibleVms(bool deleting, string code)
    {
        await using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice");
        await app.Vms.AddAsync(new("parent", "alice", 1, 1, 8, app.Clock.UtcNow, VmState.Off, null, null, IdlePolicy.Disabled, []), 5, default);
        await app.Vms.AddAsync(new("vm", "alice", 1, 1, 8, app.Clock.UtcNow, VmState.Off, null, null, IdlePolicy.Disabled, [],
            Parent: deleting ? null : "parent", Deleting: deleting, Kind: deleting ? VmKind.Primary : VmKind.Child), 5, default);
        foreach (var response in new[] { await owner.PostAsJsonAsync("/api/v1/vms/vm/source", new { commit = SourceFixture.Commit }),
            await owner.GetAsync("/api/v1/vms/vm/source/" + SourceFixture.Commit) })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }
    [Fact]
    public async Task DisabledFlagMaintenanceAndMissingSourceHaveDocumentedCodes()
    {
        await using var disabled = new TestApp(new Dictionary<string,string?> { ["Constructd:HostAdmin:Source:Enabled"] = "false" });
        using var user = await disabled.CreateUserClientAsync("alice"); await Seed(disabled);
        var health = await user.GetFromJsonAsync<JsonElement>("/api/v1/health"); Assert.DoesNotContain(health.GetProperty("apiFeatures").EnumerateArray(), x => x.GetString() == "source-cache");
        var refusal = await user.PostAsJsonAsync("/api/v1/vms/vm/source", new { commit = SourceFixture.Commit }); Assert.Equal(HttpStatusCode.Conflict, refusal.StatusCode);
        Assert.Equal("unsupported-capability", (await refusal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        await using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice"); await Seed(app);
        var missing = await owner.GetAsync("/api/v1/vms/vm/source/" + SourceFixture.Commit); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await app.Service<ISourceStore>().UpsertAsync(new(SourceFixture.Commit, SourceState.Downloading, 1, new string('b',64), "host-" + SourceFixture.Commit, null, "job", app.Clock.UtcNow, null, app.Clock.UtcNow), default);
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var inUse = await admin.DeleteAsync("/api/v1/host/source-cache/" + SourceFixture.Commit);
        Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
        Assert.Equal("source-in-use", (await inUse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var downloading = await owner.GetAsync("/api/v1/vms/vm/source/" + SourceFixture.Commit); Assert.Equal(HttpStatusCode.Conflict, downloading.StatusCode); Assert.Equal(TimeSpan.FromSeconds(5), downloading.Headers.RetryAfter!.Delta);
        await app.Service<IMaintenanceGate>().DrainAsync(TimeSpan.Zero, default);
        var closed = await owner.PostAsJsonAsync("/api/v1/vms/vm/source", new { commit = SourceFixture.Commit }); Assert.Equal(HttpStatusCode.ServiceUnavailable, closed.StatusCode);
        app.Service<IMaintenanceGate>().Reopen();
    }
}
