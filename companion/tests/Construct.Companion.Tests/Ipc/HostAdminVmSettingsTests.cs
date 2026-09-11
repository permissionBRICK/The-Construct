using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Ipc;
using Microsoft.Extensions.DependencyInjection;
using static Construct.Companion.Tests.Ipc.HttpTests;
namespace Construct.Companion.Tests.Ipc;

public sealed class HostAdminVmSettingsTests
{
    private const string Route = "/v1/hosts/host.example_7462/messages";
    private static async Task<Harness> Enroll(RoutingRemoteApi api)
    {
        var h = await Harness.Start(s => s.AddSingleton<IRemoteApi>(api));
        using var enrolled = await h.Post("/v1/hosts", new { url = "host.example", fingerprint = new string('a', 64) });
        enrolled.EnsureSuccessStatusCode();
        using var ready = await h.Post(Route, new { type = "hostadmin.ready" });
        return h;
    }
    private sealed class RemoteSettings
    {
        public readonly RoutingRemoteApi Api = new();
        public bool FailMemory;
        public int DesiredCpu = 4, DesiredRam = 8;
        public bool OldHost;
        public int MaximumCpu = 8, MaximumRam = 16;
        public RemoteSettings()
        {
            var original = Api.Handle;
            Api.Handle = request =>
            {
                object body;
                switch (request.Url.AbsolutePath)
                {
                    case "/api/v1/health": body = new { apiFeatures = OldHost ? new[] { "host-admin" } : new[] { "host-admin", "primary-cpu", "primary-memory" } }; break;
                    case "/api/v1/vms/build/cpu":
                        if (request.Method == "PUT") DesiredCpu = request.Body!.Value.GetProperty("cpus").GetInt32();
                        body = new { currentCpus = 4, desiredCpus = DesiredCpu, maximumCpus = MaximumCpu, pending = DesiredCpu != 4 }; break;
                    case "/api/v1/vms/build/memory":
                        if (request.Method == "PUT")
                        {
                            if (FailMemory) return new(400, JsonSerializer.SerializeToElement(new { code = "validation", detail = "RAM allowance changed" }));
                            DesiredRam = request.Body!.Value.GetProperty("ramGb").GetInt32();
                        }
                        body = new { currentRamGb = 8, desiredRamGb = DesiredRam, maximumRamGb = MaximumRam, pending = DesiredRam != 8 }; break;
                    case "/api/v1/vms/build/idle-policy": body = new { timeoutMinutes = 60, action = "save", maxTimeoutMinutes = 120, forceEnabled = true }; break;
                    default: return original(request);
                }
                return new(200, JsonSerializer.SerializeToElement(body));
            };
        }
    }
    private static async Task<JsonElement> Send(Harness h, string action, object args)
    {
        using var events = h.App.Services.GetRequiredService<IpcEvents>().Subscribe();
        using var response = await h.Post(Route, new { type = "hostadmin.action", action, args });
        response.EnsureSuccessStatusCode();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var e = await events.Reader.ReadAsync(timeout.Token);
            if (e.Event == "hostadmin" && e.Data.GetProperty("message").GetProperty("type").GetString() == "hostadmin.vmSettings") return e.Data.GetProperty("message");
        }
    }
    [Fact]
    public async Task DialogLoadsAndSavesAllSettingsWithoutNativeInputPromptsOrRestart()
    {
        var remote = new RemoteSettings(); await using var h = await Enroll(remote.Api);
        var result = await Send(h, "loadVmSettings", new { name = "build", requestId = "one" });
        Assert.Equal("one", result.GetProperty("requestId").GetString());
        Assert.Equal(16, result.GetProperty("settings").GetProperty("memory").GetProperty("maximumRamGb").GetInt32());
        result = await Send(h, "setVmSettings", new { name = "build", requestId = "one", cpus = 6, ramGb = 12, timeoutMinutes = 90, action = "shutdown" });
        Assert.True(result.GetProperty("saved").GetBoolean());
        Assert.Equal(new[] { "/api/v1/vms/build/cpu", "/api/v1/vms/build/memory", "/api/v1/vms/build/idle-policy" }, remote.Api.Requests.Where(r => r.Method == "PUT").Select(r => r.Url.AbsolutePath));
        Assert.Empty(h.Get<FakePrompts, IPrompts>().Shown);
        Assert.DoesNotContain(remote.Api.Requests, r => r.Url.AbsolutePath.EndsWith("/lifecycle", StringComparison.Ordinal));
    }
    [Fact]
    public async Task PartialSaveReloadsActualValuesAndRetrySkipsAlreadySavedCpu()
    {
        var remote = new RemoteSettings { FailMemory = true }; await using var h = await Enroll(remote.Api);
        var args = new { name = "build", requestId = "one", cpus = 6, ramGb = 12, timeoutMinutes = 90, action = "shutdown" };
        var result = await Send(h, "setVmSettings", args);
        Assert.False(result.GetProperty("saved").GetBoolean());
        Assert.NotEmpty(result.GetProperty("error").GetString()!);
        Assert.Equal(6, result.GetProperty("settings").GetProperty("cpu").GetProperty("desiredCpus").GetInt32());
        Assert.Equal(8, result.GetProperty("settings").GetProperty("memory").GetProperty("desiredRamGb").GetInt32());
        Assert.DoesNotContain(remote.Api.Requests, r => r.Method == "PUT" && r.Url.AbsolutePath.EndsWith("/idle-policy", StringComparison.Ordinal));
        remote.FailMemory = false;
        Assert.True((await Send(h, "setVmSettings", args)).GetProperty("saved").GetBoolean());
        Assert.Single(remote.Api.Requests, r => r.Method == "PUT" && r.Url.AbsolutePath.EndsWith("/cpu", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData(16.5, 60, "save")]
    [InlineData(17, 60, "save")]
    [InlineData(12, 0, "off")]
    [InlineData(12, 121, "save")]
    [InlineData(12, 60, "invalid")]
    public async Task EveryFieldIsValidatedBeforeTheFirstMutation(double ramGb, int timeoutMinutes, string action)
    {
        var remote = new RemoteSettings(); await using var h = await Enroll(remote.Api);
        var result = await Send(h, "setVmSettings", new { name = "build", cpus = 6, ramGb, timeoutMinutes, action });
        Assert.False(result.GetProperty("saved").GetBoolean());
        Assert.DoesNotContain(remote.Api.Requests, r => r.Method == "PUT");
    }
    [Fact]
    public async Task OlderHostOnlyLoadsAndSavesIdlePolicy()
    {
        var remote = new RemoteSettings { OldHost = true }; await using var h = await Enroll(remote.Api);
        var result = await Send(h, "loadVmSettings", new { name = "build", requestId = "old" });
        Assert.Equal(JsonValueKind.Null, result.GetProperty("settings").GetProperty("memory").ValueKind);
        result = await Send(h, "setVmSettings", new { name = "build", timeoutMinutes = 90, action = "shutdown" });
        Assert.True(result.GetProperty("saved").GetBoolean());
        Assert.Single(remote.Api.Requests, r => r.Method == "PUT");
        Assert.DoesNotContain(remote.Api.Requests, r => r.Url.AbsolutePath.EndsWith("/cpu", StringComparison.Ordinal) || r.Url.AbsolutePath.EndsWith("/memory", StringComparison.Ordinal));
    }
    [Fact]
    public async Task UnchangedHardwareAboveLoweredCapsDoesNotPreventIdleEdits()
    {
        var remote = new RemoteSettings { MaximumCpu = 0, MaximumRam = 0 }; await using var h = await Enroll(remote.Api);
        var result = await Send(h, "setVmSettings", new { name = "build", cpus = 4, ramGb = 8, timeoutMinutes = 90, action = "shutdown" });
        Assert.True(result.GetProperty("saved").GetBoolean());
        Assert.Equal("/api/v1/vms/build/idle-policy", Assert.Single(remote.Api.Requests, r => r.Method == "PUT").Url.AbsolutePath);
    }
    [Fact]
    public async Task RevokedAdminGetsDialogErrorAndNoWrites()
    {
        var remote = new RemoteSettings(); await using var h = await Enroll(remote.Api);
        var old = remote.Api.Handle;
        remote.Api.Handle = request => request.Url.AbsolutePath.EndsWith("/cpu", StringComparison.Ordinal) ? new(403) : old(request);
        var result = await Send(h, "loadVmSettings", new { name = "build", requestId = "denied" });
        Assert.NotEmpty(result.GetProperty("error").GetString()!);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("settings").ValueKind);
        result = await Send(h, "setVmSettings", new { name = "build", requestId = "denied", cpus = 6, ramGb = 12, timeoutMinutes = 90, action = "shutdown" });
        Assert.False(result.GetProperty("saved").GetBoolean());
        Assert.DoesNotContain(remote.Api.Requests, r => r.Method == "PUT");
    }
}
