using System.Text.Json;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Dispatch;
using Construct.Companion.Host.Ipc;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;
using Construct.Companion.Core.State;
using Construct.Companion.Fakes;
namespace Construct.Companion.Tests.Ipc;

public sealed class RefreshCacheTests
{
    public static IEnumerable<object[]> Policy => Parity.ParityTests.Rows("refresh-cache");
    [Theory, MemberData(nameof(Policy))]
    public void BoundariesMatchJavaScript(JsonElement row) => Assert.Equal(row.GetProperty("output").GetBoolean(),
        RefreshCachePolicy.Fresh(row.GetProperty("area").GetString()!,row.GetProperty("success").GetBoolean(),row.GetProperty("age").GetDouble()));
    [Fact]
    public async Task EnrichmentDoesNotProbeOrCollectUsageAndOnDemandUsageIsPeriodCached()
    {
        var clock = new FakeClock();
        await using var host = IpcServer.Build(s => { s.AddCompanionFakes(); s.AddSingleton<IClock>(clock); s.AddCompanionHost(runtimeJobs:false); },new(PublishEndpoint:false));
        await host.StartAsync();
        try
        {
            var entry = host.Services.GetRequiredService<CompanionInstances>().Get("agent-vm");
            var ssh = (FakeSshTransport)entry.Ssh; ssh.ScriptHandler = (_,_) => Task.FromResult(new ProcessResult(0,"{}"));
            var dispatcher = host.Services.GetRequiredService<MessageDispatcher>();
            await dispatcher.RefreshAsync(entry,CancellationToken.None,probe:false);
            for (var i=0;i<10;i++) await dispatcher.RefreshAsync(entry,CancellationToken.None,probe:false,collectUsage:false);
            Assert.Single(ssh.Scripts);
            await dispatcher.RefreshAsync(entry,CancellationToken.None,probe:false); Assert.Single(ssh.Scripts);
            entry.UsagePeriod = "monthly"; await dispatcher.RefreshAsync(entry,CancellationToken.None,probe:false); Assert.Equal(2,ssh.Scripts.Count);
            entry.UsagePeriod = "daily"; clock.Advance(TimeSpan.FromMinutes(5));
            await dispatcher.RefreshAsync(entry,CancellationToken.None,probe:false); Assert.Equal(3,ssh.Scripts.Count);
        }
        finally { await host.StopAsync(); }
    }
    [Theory]
    [InlineData("panel", true)] [InlineData("popup", true)] [InlineData("settings", true)]
    [InlineData("panel", false)] [InlineData("popup", false)] [InlineData("settings", false)]
    public async Task HttpOpeningBypassesOnceAndPublishesTheCompletedSnapshot(string view, bool explicitInstance)
    {
        var clock = new FakeClock(); var source = new FakeUpdateSource();
        await using var host = await HttpTests.Harness.Start(s => { s.AddSingleton<IClock>(clock); s.AddSingleton<IUpdateSource>(source); }, runtimeJobs: false);
        host.Files.WriteFileAtomic("/fake/scripts/.construct-settings.json", "{\"installedCommit\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}"u8);
        JsonObject Manifest(char commit) => new() { ["schemaVersion"] = 1, ["repository"] = UpdatePlanner.DefaultRepository, ["ref"] = "refs/heads/main",
            ["commit"] = new string(commit, 40), ["releaseTag"] = "host-" + new string(commit, 40),
            ["sourceAsset"] = "construct-source-" + new string(commit, 40) + ".zip", ["sourceSha256"] = new string('c', 64), ["sourceSizeBytes"] = 100,
            ["payloadAsset"] = "construct-host-" + new string(commit, 7) + "-win-x64.zip", ["payloadSha256"] = new string('d', 64), ["payloadSizeBytes"] = 100 };
        var entry = host.App.Services.GetRequiredService<CompanionInstances>().Get("agent-vm");
        ((FakeSshTransport)entry.Ssh).ScriptHandler = (_, _) => Task.FromResult(new ProcessResult(0, "{}"));
        source.Responses.Enqueue(Manifest('a'));
        using var first = await host.Post("/v1/instances/agent-vm/messages", new { type = "ready" });
        Assert.Single(source.Requests);
        source.Responses.Enqueue(Manifest('b'));
        using var opened = await host.Post("/v1/ui/activate", new { view, instance = explicitInstance ? "agent-vm" : null });
        Assert.Equal(2, source.Requests.Count);
        var snapshot = System.Text.Json.Nodes.JsonNode.Parse(await host.Client.GetStringAsync("/v1/instances/agent-vm/snapshot"));
        Assert.True(snapshot!["state"]!["state"]!["update"]!["available"]!.GetValue<bool>());
        using var ordinary = await host.Post("/v1/instances/agent-vm/messages", new { type = "ready" });
        Assert.Equal(2, source.Requests.Count);
        var desktop = host.Get<FakeCompanionDesktop, ICompanionDesktop>();
        Assert.True(Assert.Single(desktop.Activations).RefreshScheduled);
        using var admin = await host.Post("/v1/ui/activate", new { view = "hostadmin" });
        Assert.Equal(2, source.Requests.Count);
    }
    [Fact]
    public async Task BypassRefreshesTheSharedCacheWithoutAdvancingTheClock()
    {
        var source = new FakeUpdateSource(); var cache = new CachedUpdateSource(source, new FakeClock());
        var url = new Uri("https://example.test/manifest.json");
        source.Responses.Enqueue(new JsonObject { ["version"] = 1 });
        source.Responses.Enqueue(new JsonObject { ["version"] = 2 });
        await cache.GetJsonAsync(url);
        await cache.Bypass().GetJsonAsync(url);
        var result = await cache.GetJsonAsync(url);
        Assert.Equal(2, source.Requests.Count);
        Assert.Equal(2, result!["version"]!.GetValue<int>());
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ManyTicksShareOneUpstreamLookupUntilItsTtl(bool success)
    {
        var clock = new FakeClock(); var source = new FakeUpdateSource();
        source.Responses.Enqueue(success ? new JsonObject { ["version"] = "1" } : null);
        source.Responses.Enqueue(new JsonObject { ["version"] = "2" });
        var cache = new CachedUpdateSource(source,clock); var url = new Uri("https://example.test/agent/stable");
        for (var i=0;i<10;i++) { await cache.GetJsonAsync(url); clock.Advance(TimeSpan.FromSeconds(success ? 29 : 5)); }
        Assert.Single(source.Requests);
        clock.Advance(TimeSpan.FromSeconds(10)); await cache.GetJsonAsync(url); Assert.Equal(2,source.Requests.Count);
        await cache.GetJsonAsync(url); Assert.Equal(2,source.Requests.Count);
    }
}
public sealed class UpdateBannerStateTests
{
    // The panel reads update.available/behind and constructRev; both must survive the probe-driven rebuild of the state.
    [Fact]
    public async Task FoldedUpdateFieldsSurviveStateRebuilds()
    {
        var commit = new string('a', 40); var latest = new string('b', 40);
        var source = new FakeUpdateSource();
        source.Responses.Enqueue(new JsonObject { ["schemaVersion"] = 1, ["repository"] = "owner/repo", ["ref"] = "refs/heads/main", ["commit"] = latest, ["releaseTag"] = "host-" + latest,
            ["sourceAsset"] = "construct-source-" + latest + ".zip", ["sourceSha256"] = new string('c', 64), ["sourceSizeBytes"] = 1024,
            ["payloadAsset"] = "construct-host-" + latest[..7] + "-win-x64.zip", ["payloadSha256"] = new string('d', 64), ["payloadSizeBytes"] = 2048 });
        await using var host = IpcServer.Build(s => { s.AddCompanionFakes(); s.AddSingleton<IUpdateSource>(source); s.AddCompanionHost(runtimeJobs:false); }, new(PublishEndpoint:false));
        await host.StartAsync();
        try
        {
            host.Services.GetRequiredService<IStateFileSystem>().WriteFileAtomic("/fake/scripts/.construct-settings.json",
                System.Text.Encoding.UTF8.GetBytes("{\"installedCommit\":\"" + commit + "\",\"constructRef\":\"main\",\"constructRepo\":\"owner/repo\"}"));
            var entry = host.Services.GetRequiredService<CompanionInstances>().Get("agent-vm");
            ((FakeSshTransport)entry.Ssh).ScriptHandler = (_,_) => Task.FromResult(new ProcessResult(0,"{}"));
            await host.Services.GetRequiredService<MessageDispatcher>().RefreshAsync(entry,CancellationToken.None,probe:false);
            Assert.Single(source.Requests);
            await host.Services.GetRequiredService<MessageDispatcher>().RefreshAsync(entry,CancellationToken.None,probe:false);
            Assert.Single(source.Requests);
            // A freshly shown window bypasses the cache even at the same clock tick.
            source.Responses.Enqueue(null);
            await host.Services.GetRequiredService<MessageDispatcher>().DispatchAsync(entry.Name,new() { ["type"] = "ready", ["surfaceOpened"] = true },CancellationToken.None);
            Assert.Equal(2, source.Requests.Count);
            Assert.Null(host.Services.GetRequiredService<StateAggregation>().State(entry.Name)["state"]!["constructUpdate"]);
            // The next probe publishes a bare state message; the aggregation rebuilds from it plus the stored enrichment.
            host.Services.GetRequiredService<Host.Runtime.RuntimeMessageBus>().Publish("agent-vm", new { type = "state", instance = "agent-vm", state = new { online = true, vmState = "running" } });
            var rebuilt = host.Services.GetRequiredService<StateAggregation>().State("agent-vm")["state"]!.AsObject();
            Assert.True(rebuilt["update"]!["available"]!.GetValue<bool>());
            Assert.Equal("", rebuilt["update"]!["behind"]!.GetValue<string>());
            Assert.Equal("main@aaaaaaa", rebuilt["constructRev"]!.GetValue<string>());
            Assert.True(rebuilt.ContainsKey("registerOffer")); Assert.Null(rebuilt["registerOffer"]);
        }
        finally { await host.StopAsync(); }
    }
}
