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
    public async Task UpdateStartRefusalIsAVisibleNotice()
    {
        var api = new RoutingRemoteApi(); var previous = api.Handle;
        api.Handle = r => r.Url.AbsolutePath == "/api/v1/host/updates/status" ? new(200, JsonSerializer.SerializeToElement(new { current = new { state = "interrupted", updateId = "u1" } })) : previous(r);
        await using var h = await Enroll(api);
        using var ready = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.ready" }); Assert.Equal(HttpStatusCode.Accepted, ready.StatusCode);
        using var response = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.action", action = "updatesUpdate", args = new { } }); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        JsonObject? snapshot = null;
        for (var i = 0; i < 100 && snapshot?["state"]?["notice"] is null; i++) { await Task.Delay(50); snapshot = await h.Client.GetFromJsonAsync<JsonObject>("/v1/hosts/host.example_7462/snapshot"); }
        Assert.Contains("already active or needs recovery", snapshot!["state"]!["notice"]!.ToJsonString());
        Assert.DoesNotContain(api.Requests, r => r.Url.AbsolutePath == "/api/v1/host/updates/stage");
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
    [Fact]
    public async Task MalformedConfigSectionsAreRejectedBeforeQueueing()
    {
        var api = new RoutingRemoteApi(); await using var h = await Enroll(api);
        using var ready = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.ready" }); Assert.Equal(HttpStatusCode.Accepted, ready.StatusCode);
        var valid = new { key = "capacity", text = "{}" };
        foreach (var sections in new object?[] { null, Array.Empty<object>(), new object?[] { null, valid }, new object?[] { "capacity", valid }, new object?[] { new object[0], valid }, new object?[] { new { key = "capacity", text = "[]" } } })
        {
            using var response = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.action", action = "saveConfig", args = new { sections } });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalidConfig", (await response.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        }
        Assert.DoesNotContain(api.Requests, r => r.Method == "PUT" && r.Url.AbsolutePath == "/api/v1/host/config");
        using var accepted = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.action", action = "saveConfig", args = new { sections = new[] { valid } } });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); Assert.Contains(api.Requests, r => r.Method == "PUT" && r.Url.AbsolutePath == "/api/v1/host/config");
    }
    [Fact]
    public async Task ValidatedActionBodiesCarryTheClientFieldsUnchanged()
    {
        var api = new RoutingRemoteApi(); await using var h = await Enroll(api);
        using var ready = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.ready" }); Assert.Equal(HttpStatusCode.Accepted, ready.StatusCode);
        h.Get<FakePrompts, IPrompts>().Confirmations.Enqueue(true); h.Get<FakePrompts, IPrompts>().Confirmations.Enqueue(true);
        using var share = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.action", action = "shareVm", args = new { name = "build", scope = " host " } }); Assert.Equal(HttpStatusCode.Accepted, share.StatusCode);
        var sharing = Assert.Single(api.Requests, r => r.Url.AbsolutePath == "/api/v1/vms/build/sharing"); Assert.Equal(" host ", sharing.Body!.Value.GetProperty("scope").GetString());
        using var resolve = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.action", action = "updatesResolve", args = new { updateId = "u1", action = " abort " } }); Assert.Equal(HttpStatusCode.Accepted, resolve.StatusCode);
        var resolved = Assert.Single(api.Requests, r => r.Url.AbsolutePath == "/api/v1/host/updates/resolve"); Assert.Equal(" abort ", resolved.Body!.Value.GetProperty("action").GetString()); Assert.Equal("u1", resolved.Body!.Value.GetProperty("updateId").GetString());
        using var badScope = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.action", action = "shareVm", args = new { name = "build", scope = "public" } }); Assert.Equal(HttpStatusCode.BadRequest, badScope.StatusCode);
    }
    [Fact]
    public async Task ChildrenPublishWhileSshProbeWaitsAndFailedReadsKeepRowsWithProblem()
    {
        var api = new RoutingRemoteApi(); var original = api.Handle; var fail = false;
        api.Handle = r => r.Url.AbsolutePath.EndsWith("/children", StringComparison.Ordinal)
            ? fail ? new(503) : new(200, JsonSerializer.SerializeToElement(new[] { new { name = "guest", state = "running", allowedActions = new[] { "console" } } }))
            : original(r);
        await using var h = await Harness.Start(s =>
        {
            s.AddSingleton<IRemoteApi>(api);
            var files = (IStateFileSystem)s.Last(d => d.ServiceType == typeof(IStateFileSystem)).ImplementationInstance!;
            files.WriteFileAtomic("/fake/local/The-Construct/instances.json", System.Text.Encoding.UTF8.GetBytes("{\"version\":1,\"defaultInstance\":\"agent-vm\",\"instances\":{\"agent-vm\":{\"backend\":\"hyperv-remote\",\"vmName\":\"agent-vm\",\"sshHost\":\"guest.host.example\",\"scriptsDir\":\"/fake/scripts\",\"service\":{\"url\":\"https://host.example:7462\",\"auth\":\"negotiate\"}}}}"));
            RemoteHost.WritePin(files, "https://host.example:7462", new string('a', 64));
        });
        var entry = h.App.Services.GetRequiredService<CompanionInstances>().Get("agent-vm");
        var ssh = (FakeSshTransport)entry.Ssh;
        var pending = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ssh.ScriptHandler = (_, ct) => { began.TrySetResult(); return pending.Task.WaitAsync(ct); };
        using var stream = await h.Client.GetAsync("/v1/events", HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync()); await reader.ReadLineAsync(); await reader.ReadLineAsync();
        var refresh = h.App.Services.GetRequiredService<MessageDispatcher>().RefreshAsync(entry, default, collectUsage: false);
        try
        {
            await began.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var message = (await Until(reader, d => d["message"]?["type"]?.GetValue<string>() == "children"))["message"]!;
            Assert.Equal("agent-vm", message["instance"]!.GetValue<string>());
            Assert.Equal(new[] { "children", "instance", "type" }, message.AsObject().Select(p => p.Key).Order());
            Assert.Equal("guest", message["children"]!["items"]![0]!["name"]!.GetValue<string>());
            Assert.True(message["children"]!["items"]![0]!["canConsole"]!.GetValue<bool>());
            Assert.False(refresh.IsCompleted);
        }
        finally { pending.TrySetResult(new ProcessResult(255)); await refresh; }
        fail = true;
        await h.App.Services.GetRequiredService<HostAdministration>().RefreshExtrasAsync(entry, default);
        var snapshot = await h.Client.GetFromJsonAsync<JsonObject>("/v1/instances/agent-vm/snapshot");
        Assert.Equal("guest", snapshot!["children"]!["children"]!["items"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("The Construct host service answered HTTP 503 (GET /api/v1/vms/agent-vm/children)", snapshot["children"]!["children"]!["problem"]!.GetValue<string>());
    }
    [Theory]
    [InlineData("max", 8, true)]
    [InlineData("3", 3, true)]
    [InlineData("9", 0, false)]
    [InlineData("1.5", 0, false)]
    [InlineData(null, 0, false)]
    public async Task CpuPromptUsesDefaultsAndValidatesTheAllowance(string? input, int expected, bool saved)
    {
        var api = new RoutingRemoteApi(); var original = api.Handle;
        api.Handle = r => r.Url.AbsolutePath.EndsWith("/cpu", StringComparison.Ordinal) ? new(200, JsonSerializer.SerializeToElement(new { currentCpus = 2, maximumCpus = 8, recommendedCpus = 8, pending = true, desiredCpus = 4 })) : original(r);
        await using var h = await Enroll(api);
        using var ready = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.ready" });
        var prompts = h.Get<FakePrompts, IPrompts>(); prompts.Inputs.Enqueue(input);
        using var response = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.action", action = "changeVmCpu", args = new { name = "build" } });
        var prompt = Assert.Single(prompts.Shown.OfType<InputPrompt>());
        Assert.Equal("CPU count for build", prompt.Title);
        Assert.Equal("Current: 2. Allowed maximum: 8. Enter a count or max. Applies on the next full stop/start; an Ubuntu reboot is insufficient.", prompt.Prompt);
        Assert.Equal("4", prompt.Value);
        var writes = api.Requests.Where(r => r.Method == "PUT" && r.Url.AbsolutePath.EndsWith("/cpu", StringComparison.Ordinal)).ToArray();
        if (saved)
        {
            Assert.Equal(expected, Assert.Single(writes).Body!.Value.GetProperty("cpus").GetDouble());
            var snapshot = await h.Client.GetFromJsonAsync<JsonObject>("/v1/hosts/host.example_7462/snapshot");
            Assert.Equal("build: 4 vCPUs saved for the next full stop/start. The running VM is unchanged.", snapshot!["state"]!["notice"]!["text"]!.GetValue<string>());
        }
        else Assert.Empty(writes);
    }
    [Theory]
    [InlineData(0, false)][InlineData(1, true)][InlineData(64, true)][InlineData(65, false)][InlineData(2.5, false)]
    public async Task DirectCpuActionEnforcesWholeCounts(double cpus, bool saved)
    {
        var api = new RoutingRemoteApi(); await using var h = await Enroll(api);
        using var ready = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.ready" });
        using var response = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.action", action = "setVmCpu", args = new { name = "build", cpus } });
        Assert.Equal(saved, api.Requests.Any(r => r.Method == "PUT" && r.Url.AbsolutePath == "/api/v1/vms/build/cpu"));
        if (!saved)
        {
            var snapshot = await h.Client.GetFromJsonAsync<JsonObject>("/v1/hosts/host.example_7462/snapshot");
            Assert.Equal("Choose a whole CPU count from 1 to 64.", snapshot!["state"]!["notice"]!["text"]!.GetValue<string>());
        }
    }
    [Theory]
    [InlineData("restartVm", true)][InlineData("restartVm", false)][InlineData("startVm", true)][InlineData("startVm", false)]
    public async Task CpuLifecycleRequiresConfirmation(string action, bool confirm)
    {
        var api = new RoutingRemoteApi(); await using var h = await Enroll(api);
        using var ready = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.ready" });
        var prompts = h.Get<FakePrompts, IPrompts>(); prompts.Confirmations.Enqueue(confirm);
        using var response = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.action", action, args = new { name = "build" } });
        var restart = action == "restartVm";
        Assert.Contains(((restart ? "Restart" : "Start") + " \"build\"?", restart
            ? "Construct will ask Ubuntu to shut down, apply any pending CPU and RAM settings, then start the VM. Running work will be interrupted."
            : "Construct will apply any pending CPU and RAM settings before starting this powered-off VM."), prompts.Shown);
        var requests = api.Requests.Where(r => r.Url.AbsolutePath == "/api/v1/vms/build/lifecycle").ToArray();
        if (confirm) Assert.Equal(restart ? "restart" : "start", Assert.Single(requests).Body!.Value.GetProperty("action").GetString());
        else Assert.Empty(requests);
    }
    [Theory]
    [InlineData("updatesCheck", "/api/v1/host/updates/check")]
    [InlineData("updatesStage", "/api/v1/host/updates/stage")]
    public async Task MaintenanceActionsOmitAnEmptyReleaseTag(string action, string route)
    {
        var api = new RoutingRemoteApi(); await using var h = await Enroll(api);
        using var ready = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.ready" }); Assert.Equal(HttpStatusCode.Accepted, ready.StatusCode);
        foreach (var tag in new[] { "", "  ", "host-" + new string('b', 40) })
        {
            api.Requests.Clear();
            using var response = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.action", action, args = new { releaseTag = tag } }); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            RemoteRequest? sent = null;
            for (var i = 0; i < 100 && sent is null; i++) { await Task.Delay(50); lock (api.Requests) sent = api.Requests.FirstOrDefault(r => r.Method == "POST" && r.Url.AbsolutePath == route); }
            Assert.NotNull(sent);
            var hasTag = sent.Body is { } body && body.TryGetProperty("releaseTag", out var value) && value.ValueKind == JsonValueKind.String;
            Assert.Equal(tag.Trim().Length > 0, hasTag);
        }
    }
    [Fact]
    public async Task OpeningTheHostChecksForTheLatestReleaseAtMostEveryFifteenMinutes()
    {
        var api = new RoutingRemoteApi(); var previous = api.Handle; var installed = new string('1', 40); var latest = new string('2', 40);
        api.Handle = r => r.Url.AbsolutePath switch
        {
            "/api/v1/host/updates/status" => new(200, JsonSerializer.SerializeToElement(new { installed = new { commit = installed, packageVersion = "1" }, current = (object?)null, history = Array.Empty<object>(), latestKnown = new { commit = installed, packageVersion = "1" } })),
            "/api/v1/host/updates/check" => new(200, JsonSerializer.SerializeToElement(new { installed = new { commit = installed, packageVersion = "1" }, latest = new { commit = latest, packageVersion = "2", releaseTag = "host-" + latest, compatible = true, reasons = Array.Empty<string>() }, checkedAt = "2026-09-12T00:00:00+00:00" })),
            _ => previous(r)
        };
        var clock = new FakeClock(); await using var h = await Enroll(api, s => s.AddSingleton<IClock>(clock));
        async Task<JsonObject> Ready()
        {
            using var ready = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.ready" }); Assert.Equal(HttpStatusCode.Accepted, ready.StatusCode);
            JsonObject? snapshot = null;
            for (var i = 0; i < 100 && snapshot?["state"]?["maintenanceTab"]?["updateAvailable"]?.GetValue<bool>() != true; i++) { await Task.Delay(50); snapshot = await h.Client.GetFromJsonAsync<JsonObject>("/v1/hosts/host.example_7462/snapshot"); }
            return snapshot!["state"]!.AsObject();
        }
        var state = await Ready();
        Assert.True(state["maintenanceTab"]!["updateAvailable"]!.GetValue<bool>());
        Assert.Equal(latest[..12], state["maintenanceTab"]!["latestKnown"]!["commit"]!.GetValue<string>());
        Assert.Equal("", state["updateError"]!.GetValue<string>()); Assert.False(state["updateChecking"]!.GetValue<bool>());
        int Checks() { lock (api.Requests) return api.Requests.Count(r => r.Method == "POST" && r.Url.AbsolutePath == "/api/v1/host/updates/check"); }
        Assert.Equal(1, Checks());
        await Ready(); await Task.Delay(200); Assert.Equal(1, Checks());
        clock.Advance(TimeSpan.FromMinutes(16));
        await Ready();
        for (var i = 0; i < 100 && Checks() < 2; i++) await Task.Delay(50);
        Assert.Equal(2, Checks());
        var body = api.Requests.Last(r => r.Url.AbsolutePath == "/api/v1/host/updates/check").Body;
        Assert.False(body is { } b && b.TryGetProperty("releaseTag", out _));
    }
    [Fact]
    public async Task ClosingTheWindowForgetsTabAndNotice()
    {
        var api = new RoutingRemoteApi(); await using var h = await Enroll(api);
        using var ready = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.ready" }); Assert.Equal(HttpStatusCode.Accepted, ready.StatusCode);
        using var tab = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.tab", tab = "users" }); Assert.Equal(HttpStatusCode.Accepted, tab.StatusCode);
        using var bad = await h.Post("/v1/hosts/host.example_7462/messages", new { type = "hostadmin.action", action = "nonsense", args = new { } }); Assert.Equal(HttpStatusCode.Accepted, bad.StatusCode);
        JsonObject? snapshot = null;
        for (var i = 0; i < 100 && (snapshot?["state"]?["activeTab"]?.GetValue<string>() != "users" || snapshot?["state"]?["notice"] is null); i++) { await Task.Delay(50); snapshot = await h.Client.GetFromJsonAsync<JsonObject>("/v1/hosts/host.example_7462/snapshot"); }
        Assert.Equal("users", snapshot!["state"]!["activeTab"]!.GetValue<string>()); Assert.NotNull(snapshot["state"]!["notice"]);
        h.App.Services.GetRequiredService<HostAdministration>().Close("host.example_7462");
        snapshot = await h.Client.GetFromJsonAsync<JsonObject>("/v1/hosts/host.example_7462/snapshot");
        Assert.Equal("overview", snapshot!["state"]!["activeTab"]!.GetValue<string>()); Assert.Null(snapshot["state"]!["notice"]);
    }
    private static async Task<Harness> Enroll(RoutingRemoteApi api, Action<IServiceCollection>? configure = null)
    { var h = await Harness.Start(s => { s.AddSingleton<IRemoteApi>(api); configure?.Invoke(s); }); using var response = await h.Post("/v1/hosts", new { url = "host.example", fingerprint = new string('a', 64) }); Assert.Equal(HttpStatusCode.Created, response.StatusCode); return h; }
}
