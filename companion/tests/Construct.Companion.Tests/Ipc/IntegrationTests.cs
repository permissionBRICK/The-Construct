using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Core.Notifications;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Desktop;
using Construct.Companion.Host.Dispatch;
using Construct.Companion.Host.Ipc;
using Construct.Companion.Host.Runtime;
using Microsoft.Extensions.DependencyInjection;
using static Construct.Companion.Tests.Ipc.HttpTests;
namespace Construct.Companion.Tests.Ipc;

public sealed class IntegrationTests
{
    private static async Task Eventually(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MixedAndRemoteOnlyRuntimeRoundTrip(bool remoteOnly)
    {
        await using var h = await Harness.Start(s => s.AddCompanionFakes(true, remoteOnly));
        var instances = h.App.Services.GetRequiredService<CompanionInstances>();
        Assert.Equal(remoteOnly ? ["remote-vm"] : new[] { "agent-vm", "remote-vm" }, instances.Names);
        var entry = instances.Get("remote-vm");
        await Eventually(() => entry.Runtime?.Forwarder is not null);
        var connections = h.Get<FakeInstanceConnections, IInstanceConnections>();
        var ssh = connections.Transports["remote-vm"];
        await Eventually(() => ssh.Watches.Count > 0);
        var snapshot = await h.Client.GetFromJsonAsync<Snapshot>("/v1/instances/remote-vm/snapshot");
        Assert.Equal("hyperv-remote", snapshot!.State!.Value.GetProperty("state").GetProperty("backend").GetString());
        Assert.Equal(JsonValueKind.Null, snapshot.State!.Value.GetProperty("state").GetProperty("connectedInstance").ValueKind);
        var transport = connections.Forwards["remote-vm"];
        using var stream = await h.Client.GetAsync("/v1/events?instance=remote-vm", HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync()); await reader.ReadLineAsync(); await reader.ReadLineAsync();
        transport.View["requests"] = new JsonArray(new JsonObject { ["v"] = 1, ["id"] = "web", ["vmPort"] = 5173 });
        await entry.Runtime!.Forwarder!.ReconcileAsync();
        await Until(reader, d => d["message"]?["forwards"]?["items"] is JsonArray a && a.Count == 1 && a[0]?["status"]?.GetValue<string>() == "open");
        using var open = await h.Post("/v1/instances/remote-vm/messages", new { type = "command", id = "openForward", forward = "web" });
        Assert.Single(h.Get<FakeLauncher, ILauncher>().Opened);
        using var close = await h.Post("/v1/instances/remote-vm/messages", new { type = "command", id = "closeForward", forward = "web" });
        Assert.Empty(transport.View["requests"]!.AsArray());
        ssh.Watches.Single(w => w.Script == NotificationProtocol.WatchScript()).Process.Emit("{\"title\":\"Done\",\"body\":\"Build finished\"}\n");
        await Eventually(() => h.Get<FakeToastRaiser, IToastRaiser>().Toasts.Count == 1);
        using var audio = await h.Post("/v1/instances/remote-vm/messages", new { type = "setAudio", enabled = true });
        Assert.True(entry.Runtime.Audio!.Status.Enabled);
        using var off = await h.Post("/v1/instances/remote-vm/messages", new { type = "setAudio", enabled = false });
        Assert.False(entry.Runtime.Audio!.Status.Enabled);
        using var preferences = await h.Client.PutAsJsonAsync("/v1/settings", new { notifications = false, forwards = new { enabled = false }, micDevice = "new-device", repatchDelaySeconds = 12 });
        await h.App.Services.GetRequiredService<RuntimeSupervisor>().RefreshAsync();
        Assert.False(entry.Runtime!.Instance.ForwardsEnabled); Assert.False(entry.Runtime.Instance.NotificationsEnabled);
        Assert.Equal(12, entry.Runtime.Instance.RepatchDelaySeconds);
        Assert.True(ssh.Watches.All(w => w.Process.Stopped));
        await h.App.StopAsync();
        Assert.False(h.Files.FileExists(h.EndpointPath));
        Assert.True(transport.Ssh.Tunnels.All(t => t.Process.Stopped));
    }
    public static IEnumerable<object[]> Settings => Parity.ParityTests.Rows("integration-settings");
    [Theory, MemberData(nameof(Settings))]
    public async Task SaveSettingsMatchesJavaScriptWriterBytes(JsonElement row)
    {
        await using var h = await Harness.Start(s => s.AddCompanionFakes(true));
        var name = row.GetProperty("name").GetString()!;
        using var response = await h.Post($"/v1/instances/{name}/messages", new { type = "saveSettings", settings = row.GetProperty("input") });
        response.EnsureSuccessStatusCode();
        Assert.Equal(row.GetProperty("install").GetString(), Encoding.UTF8.GetString(h.Files.ReadFile("/fake/scripts/.construct-settings.json")!));
        if (name != "agent-vm") Assert.Equal(row.GetProperty("instance").GetString(), Encoding.UTF8.GetString(h.Files.ReadFile($"/fake/local/The-Construct/instances/{name}.json")!));
    }
    [Fact]
    public async Task DesktopSinkUsesAggregatedEventsAndDispatcher()
    {
        await using var h = await Harness.Start();
        var sink = h.App.Services.GetRequiredService<IMessageSink>();
        Assert.IsType<DispatcherMessageSink>(sink);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        await using var messages = sink.Subscribe("agent-vm", stop.Token).GetAsyncEnumerator(stop.Token);
        var next = messages.MoveNextAsync();
        await sink.PostAsync("agent-vm", JsonSerializer.SerializeToElement(new { type = "ready" }), stop.Token);
        Assert.True(await next);
        Assert.Equal("state", messages.Current.GetProperty("type").GetString());
        Assert.Equal("agent-vm", messages.Current.GetProperty("state").GetProperty("instance").GetString());
    }
    [Fact]
    public async Task RemoteOnlySelfTestDoesNotRequireLocalHyperV()
    {
        await using var h = await Harness.Start(s => s.AddCompanionFakes(true, true));
        var platform = new DesktopSelfTestPlatform((IStateFileSystem)h.Files, new FakeProcessRunner(), new FakeHypervisorState(), new FakeDesktopProcess(), () => "fixture",
            (_, _) => Task.FromResult(HypervisorState.Running));
        var report = await new SelfTest((IStateFileSystem)h.Files, platform, new FakeAudioCapture(), new FakeToastRaiser()).RunAsync();
        Assert.Equal(0, report.ExitCode);
        Assert.Contains(report.Checks, c => c.Name == "hypervisor:remote-vm" && c.Status == "running");
        Assert.DoesNotContain(report.Checks, c => c.Name == "hypervisor:agent-vm");
    }
}
