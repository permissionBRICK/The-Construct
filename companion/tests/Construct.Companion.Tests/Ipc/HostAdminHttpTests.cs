using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Dispatch;
using Construct.Companion.Core.Remote;
using Microsoft.Extensions.DependencyInjection;
using static Construct.Companion.Tests.Ipc.HttpTests;
namespace Construct.Companion.Tests.Ipc;
public sealed class HostAdminHttpTests
{
    [Fact]
    public async Task QueuedSignInFailureKeepsFullAdminState()
    {
        var api = new RoutingRemoteApi(); await using var h = await Enroll(api);
        using var ready = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.ready" });
        var handle = api.Handle; api.Handle = request => request.Url.AbsolutePath == "/api/v1/whoami" ? new(403) : handle(request);
        h.Get<FakePrompts, IPrompts>().Picks.Enqueue(["negotiate"]);
        using var stream = await h.Client.GetAsync("/v1/events", HttpCompletionOption.ResponseHeadersRead); using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync()); await reader.ReadLineAsync(); await reader.ReadLineAsync();
        using var response = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.signIn" }); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        h.App.Services.GetRequiredService<Host.Ipc.IpcEvents>().Companion(new { type = "testBarrier" });
        var states = 0;
        while (true)
        {
            var item = await ReadEvent(reader); if (item.Data["type"]?.GetValue<string>() == "testBarrier") break;
            if (item.Data["message"]?["type"]?.GetValue<string>() != "hostadmin.state") continue;
            states++; var state = item.Data["message"]!["state"]!; Assert.Equal("admin", state["mode"]!.GetValue<string>()); Assert.NotEmpty(state["tabs"]!.AsArray()); Assert.NotNull(state["notice"]);
        }
        Assert.Equal(1, states);
    }
    [Fact]
    public async Task RemoteStartUsesPowerRouteAndOffersOnlyDetectOncePerHost()
    {
        var api = new RoutingRemoteApi();
        await using var h = await Harness.Start(s =>
        {
            s.AddSingleton<IRemoteApi>(api);
            var files = (IStateFileSystem)s.Last(d => d.ServiceType == typeof(IStateFileSystem)).ImplementationInstance!;
            files.WriteFileAtomic("/fake/local/The-Construct/instances.json", System.Text.Encoding.UTF8.GetBytes("{\"version\":1,\"defaultInstance\":\"agent-vm\",\"instances\":{\"agent-vm\":{\"backend\":\"hyperv-remote\",\"vmName\":\"agent-vm\",\"sshHost\":\"guest.host.example\",\"scriptsDir\":\"/fake/scripts\",\"service\":{\"url\":\"https://host.example:7462\",\"auth\":\"negotiate\"}}}}"));
            RemoteHost.WritePin(files, "https://host.example:7462", new string('a', 64));
        });
        using var start = await h.Post("/v1/instances/agent-vm/messages", new { type = "command", id = "startConnect" }); Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        Assert.Contains(api.Requests, r => r.Method == "POST" && r.Url.AbsolutePath == "/api/v1/vms/agent-vm/power");
        Assert.Empty(h.Get<FakeLauncher, ILauncher>().Elevated); Assert.Empty(h.Get<FakeLauncher, ILauncher>().Detached);
        var hosts = h.App.Services.GetRequiredService<HostAdministration>(); var entry = h.App.Services.GetRequiredService<CompanionInstances>().Get("agent-vm");
        await hosts.RefreshExtrasAsync(entry, default); await hosts.RefreshExtrasAsync(entry, default);
        Assert.Single(api.Requests, r => r.Url.AbsolutePath == "/api/v1/whoami");
        Assert.DoesNotContain(api.Requests, r => r.Url.AbsolutePath == "/api/v1/host/status");
        var snapshot = await h.Client.GetFromJsonAsync<JsonObject>("/v1/instances/agent-vm/snapshot");
        Assert.Equal("host.example", snapshot!["hostAdminOffer"]!["offer"]!["host"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(snapshot["hostAdminOffer"]!["offer"], snapshot["state"]!["state"]!["hostAdminOffer"]));
    }
    [Fact]
    public async Task SignInRetainsModelAndSelectedTab()
    {
        var api = new RoutingRemoteApi(); await using var h = await Enroll(api);
        using var ready = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.ready" });
        using var tab = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.tab", tab = "vms" });
        h.Get<FakePrompts, IPrompts>().Picks.Enqueue(["negotiate"]);
        using var signIn = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.signIn" });
        var snapshot = await h.Client.GetFromJsonAsync<JsonObject>("/v1/hosts/host.example_7462/snapshot");
        Assert.Equal("admin", snapshot!["state"]!["mode"]!.GetValue<string>()); Assert.Equal("vms", snapshot["state"]!["activeTab"]!.GetValue<string>());
    }
    [Fact]
    public async Task EnrollmentPinsBeforeCredentialAndRemovalDeletesCredential()
    {
        var api = new RoutingRemoteApi(); await using var h = await Harness.Start(s => s.AddSingleton<IRemoteApi>(api));
        using var add = await h.Post("/v1/hosts", new { url = "https://host.example:7462", token = "sample-test-only", fingerprint = new string('a', 64) });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        Assert.Null(api.Requests[0].Token); Assert.Equal(RemoteAuthentication.None, api.Requests[0].Authentication);
        Assert.NotNull(api.Requests[1].Token); Assert.Equal(RemoteAuthentication.Token, api.Requests[1].Authentication);
        var list = await h.Client.GetFromJsonAsync<JsonArray>("/v1/hosts"); Assert.Single(list!); Assert.True(list![0]!["pinned"]!.GetValue<bool>());
        var tokens = h.App.Services.GetRequiredService<ITokenStore>(); Assert.NotNull(await tokens.ReadAsync("host.example_7462"));
        using var remove = await h.Client.DeleteAsync("/v1/hosts/host.example_7462"); Assert.Equal(HttpStatusCode.Accepted, remove.StatusCode); Assert.Null(await tokens.ReadAsync("host.example_7462"));
        Assert.Empty((await h.Client.GetFromJsonAsync<JsonArray>("/v1/hosts"))!);
    }
    [Fact]
    public async Task PinMismatchCannotSendCredentialOrPersistEnrollment()
    {
        var api = new RoutingRemoteApi(); await using var h = await Harness.Start(s => s.AddSingleton<IRemoteApi>(api));
        using var add = await h.Post("/v1/hosts", new { url = "host.example", token = "sample-test-only", fingerprint = new string('b', 64) }); Assert.False(add.IsSuccessStatusCode);
        Assert.Empty(api.Requests); Assert.Empty((await h.Client.GetFromJsonAsync<JsonArray>("/v1/hosts"))!);
    }
    [Theory]
    [InlineData("overview")][InlineData("vms")][InlineData("users")][InlineData("media")][InlineData("operations")][InlineData("config")][InlineData("maintenance")]
    public async Task EveryTabLoads(string tab)
    {
        var api = new RoutingRemoteApi(); await using var h = await Enroll(api);
        using var ready = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.ready" }); Assert.Equal(HttpStatusCode.Accepted, ready.StatusCode);
        using var select = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.tab", tab }); Assert.Equal(HttpStatusCode.Accepted, select.StatusCode);
        var snapshot = await h.Client.GetFromJsonAsync<JsonObject>("/v1/hosts/host.example_7462/snapshot"); Assert.Equal("hostadmin.state", snapshot!["type"]!.GetValue<string>()); Assert.Equal(tab, snapshot["state"]!["activeTab"]!.GetValue<string>()); Assert.NotNull(snapshot["state"]![tab == "maintenance" ? "maintenanceTab" : tab]);
    }
    [Theory]
    [InlineData("shareVm", "PUT", "/api/v1/vms/build/sharing")]
    [InlineData("shutdownVm", "POST", "/api/v1/vms/build/lifecycle")]
    [InlineData("deleteVm", "DELETE", "/api/v1/vms/build")]
    [InlineData("cancelJob", "POST", "/api/v1/jobs/j1/cancel")]
    [InlineData("mediaCleanup", "POST", "/api/v1/media/cleanup")]
    [InlineData("deleteMedia", "DELETE", "/api/v1/media/j1")]
    [InlineData("updateUser", "PUT", "/api/v1/users/build")]
    [InlineData("createUser", "POST", "/api/v1/users")]
    [InlineData("deleteUser", "DELETE", "/api/v1/users/build")]
    [InlineData("saveAllowance", "PUT", "/api/v1/users/build/allowance")]
    [InlineData("saveOverrides", "PUT", "/api/v1/vms/build/overrides")]
    [InlineData("clearOverrides", "DELETE", "/api/v1/vms/build/overrides")]
    [InlineData("revokeToken", "DELETE", "/api/v1/users/build/tokens/j1")]
    [InlineData("revokeVmToken", "DELETE", "/api/v1/vms/build/token")]
    [InlineData("renewVmLease", "POST", "/api/v1/vms/build/lease")]
    [InlineData("updatesCheck", "POST", "/api/v1/host/updates/check")]
    [InlineData("updatesStage", "POST", "/api/v1/host/updates/stage")]
    [InlineData("updatesApply", "POST", "/api/v1/host/updates/apply")]
    [InlineData("updatesCancel", "POST", "/api/v1/host/updates/cancel")]
    [InlineData("updatesResolve", "POST", "/api/v1/host/updates/resolve")]
    public async Task ActionRoundTrip(string action, string method, string route)
    {
        var api = new RoutingRemoteApi(); await using var h = await Enroll(api);
        using var ready = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.ready" }); Assert.Equal(HttpStatusCode.Accepted, ready.StatusCode);
        using var stream = await h.Client.GetAsync("/v1/events", HttpCompletionOption.ResponseHeadersRead); using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync()); await reader.ReadLineAsync(); await reader.ReadLineAsync();
        h.Get<FakePrompts, IPrompts>().Confirmations.Enqueue(true);
        using var response = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.action", action, args = new { name = "build", id = "j1", scope = "host", lifetime = "24h", updateId = "u1", action = "abort", form = new { name = "alice", role = "user" } } });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); Assert.Contains(api.Requests, r => r.Method == method && r.Url.AbsolutePath == route);
        var e = await Until(reader, d => d["host"]?.GetValue<string>() == "host.example_7462"); Assert.Equal("hostadmin.state", e["message"]!["type"]!.GetValue<string>());
    }
    [Fact]
    public async Task RoleRefusalReclassifiesAndBlocksFurtherAdminMutation()
    {
        var api = new RoutingRemoteApi(); await using var h = await Enroll(api);
        using var ready = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.ready" }); Assert.Equal(HttpStatusCode.Accepted, ready.StatusCode);
        var previous = api.Handle; api.Handle = request => request.Method == "PUT" ? new(403, JsonSerializer.SerializeToElement(new { code = "forbidden" })) : previous(request);
        using var first = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.action", action = "shareVm", args = new { name = "build", scope = "host" } }); Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var snapshot = await h.Client.GetFromJsonAsync<JsonObject>("/v1/hosts/host.example_7462/snapshot"); Assert.Equal("user", snapshot!["state"]!["mode"]!.GetValue<string>());
        var count = api.Requests.Count;
        using var second = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.action", action = "shareVm", args = new { name = "build", scope = "host" } }); Assert.Equal(HttpStatusCode.Accepted, second.StatusCode); Assert.Equal(count, api.Requests.Count);
    }
    private static async Task<Harness> Enroll(RoutingRemoteApi api)
    { var h = await Harness.Start(s => s.AddSingleton<IRemoteApi>(api)); using var response = await h.Post("/v1/hosts", new { url = "host.example", fingerprint = new string('a', 64) }); Assert.Equal(HttpStatusCode.Created, response.StatusCode); return h; }
}
