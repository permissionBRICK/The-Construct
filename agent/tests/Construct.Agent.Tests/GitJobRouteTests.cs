using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Construct.Agent.Tests.Support;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Construct.Agent.Tests;

/// <summary>
/// The agent's job routes (plan 7.4): a provision, a reinstall, a create with settings and a retry started from the device
/// carry the clone credentials of the device's git helper for the hosts of the VM's profiles; everything else passes as
/// sent and the engine's answer comes back with the hosts that had none. The engine is the real one, its job starts
/// answered by a recorder in front of it.
/// </summary>
public sealed class GitJobRouteTests : IAsyncDisposable
{
    private const string Token = "route-clone-token-7788";
    private readonly string _bin = Path.Combine(Path.GetTempPath(), "agent-test-git-" + Guid.NewGuid().ToString("n"));
    private readonly JobRecorder _recorder = new();
    private EngineHost? _engine;
    private TestAgent? _laptop;

    private static string Creds => Convert.ToBase64String(Encoding.UTF8.GetBytes($"https://alice:{Token}@git.example.test"));

    private async Task<(EngineHost Engine, TestAgent Laptop, string EngineId)> StartAsync()
    {
        Directory.CreateDirectory(_bin);
        FakeGit.Install(_bin);
        FakeGit.Helper(_bin, "git.example.test", "alice", Token);
        FakeGit.Accept(_bin, "git.example.test", "alice", Token);
        _engine = new EngineHost(services => services.AddSingleton<IStartupFilter>(_recorder));
        var signIn = await _engine.AddUserAsync("alice");
        _laptop = await TestAgent.StartAsync(gitSearchPath: _bin);
        var engineId = await _laptop.AddEngineAsync(_engine, signIn);
        await _laptop.PostAsync("api/device/setup", new { engine = engineId, deviceName = "laptop" });

        // The VM, a profile with a private and a public-host repo, and the VM's stored settings.
        using (var created = await _laptop.Api.PostAsJsonAsync($"api/engines/{engineId}/api/v1/vms", new { name = "dev", cpu = 2, ramGb = 4, diskGb = 40 }))
            Assert.True(created.StatusCode == HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        await PutAsync(_laptop, $"api/engines/{engineId}/api/v1/profiles/web", new
        {
            profile = new { name = "web", repos = new object[] { new { url = "https://git.example.test/team/app.git" }, new { url = "https://other.example.test/x.git" } } },
        });
        await PutAsync(_laptop, $"api/engines/{engineId}/api/v1/vms/dev/desired", new { projects = new[] { "web" }, settings = new { } });
        _recorder.On = true;
        return (_engine, _laptop, engineId);
    }

    private static async Task PutAsync(TestAgent laptop, string path, object body)
    {
        using var response = await laptop.Api.PutAsJsonAsync(path, body);
        Assert.True(response.IsSuccessStatusCode, $"PUT {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private static string? SentCreds(JsonObject sent) => sent["secrets"]?["gitCloneCredentialsB64"]?.GetValue<string>();

    [Fact]
    public async Task A_provision_from_the_device_carries_the_credentials_and_every_other_field_as_sent()
    {
        var (_, laptop, engine) = await StartAsync();
        var body = new JsonObject
        {
            ["kind"] = "provision", ["requestId"] = "click-1", ["commit"] = new string('a', 40), ["deferProjectCommands"] = true,
            ["desired"] = new JsonObject { ["projects"] = new JsonArray("web"), ["settings"] = new JsonObject { ["t3Code"] = "true" } },
            ["secrets"] = new JsonObject { ["sudoPassword"] = "pw" }, ["somethingNew"] = new JsonObject { ["x"] = 1 },
        };

        var answer = await laptop.PostAsync($"api/engines/{engine}/vms/dev/jobs", body, HttpStatusCode.Accepted);

        Assert.Equal("recorded-job", answer["jobId"]!.GetValue<string>());
        Assert.Equal(["https://other.example.test"], answer["hostsWithoutCredential"]!.AsArray().Select(h => h!.GetValue<string>()));
        var (path, sent) = Assert.Single(_recorder.Posts);
        Assert.Equal("/api/v1/vms/dev/jobs", path);
        Assert.Equal(Creds, SentCreds(sent));
        // Only the credential was added.
        sent["secrets"]!.AsObject().Remove("gitCloneCredentialsB64");
        Assert.True(JsonNode.DeepEquals(body, sent), sent.ToJsonString());
        Assert.DoesNotContain(Token, answer.ToJsonString());
        Assert.DoesNotContain(Token, laptop.StateFileText());
        Assert.DoesNotContain(FakeGit.Calls(_bin), c => c.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reinstall_without_settings_uses_the_profiles_the_vm_has_stored()
    {
        var (_, laptop, engine) = await StartAsync();

        await laptop.PostAsync($"api/engines/{engine}/vms/dev/jobs", new { kind = "reinstall", requestId = "r1", freshImage = true }, HttpStatusCode.Accepted);

        var (_, sent) = Assert.Single(_recorder.Posts);
        Assert.Equal(Creds, SentCreds(sent));
        Assert.True(sent["freshImage"]!.GetValue<bool>());
        Assert.Null(sent["desired"]);
    }

    [Fact]
    public async Task A_body_that_carries_credentials_keeps_them_and_a_restore_gets_none()
    {
        var (_, laptop, engine) = await StartAsync();

        await laptop.PostAsync($"api/engines/{engine}/vms/dev/jobs",
            new { kind = "provision", requestId = "own", secrets = new { gitCloneCredentialsB64 = "b3du" } }, HttpStatusCode.Accepted);
        await laptop.PostAsync($"api/engines/{engine}/vms/dev/jobs",
            new { kind = "restore", requestId = "rs", backupId = new string('b', 32), secrets = new { backupKey = "k" } }, HttpStatusCode.Accepted);

        Assert.Equal("b3du", SentCreds(_recorder.Posts[0].Body));
        Assert.Null(SentCreds(_recorder.Posts[1].Body));
        Assert.Equal("k", _recorder.Posts[1].Body["secrets"]!["backupKey"]!.GetValue<string>());
        Assert.Empty(FakeGit.Calls(_bin));
    }

    [Fact]
    public async Task A_create_with_settings_carries_credentials_for_its_profiles_and_one_without_settings_none()
    {
        var (_, laptop, engine) = await StartAsync();

        var answer = await laptop.PostAsync($"api/engines/{engine}/vms",
            new { name = "fresh", ramGb = 4, desired = new { projects = new[] { "web" }, settings = new { } } }, HttpStatusCode.Accepted);
        await laptop.PostAsync($"api/engines/{engine}/vms", new { name = "plain", ramGb = 4 }, HttpStatusCode.Accepted);

        Assert.Equal("recorded-job", answer["jobId"]!.GetValue<string>());
        Assert.Equal("/api/v1/vms", _recorder.Posts[0].Path);
        Assert.Equal(Creds, SentCreds(_recorder.Posts[0].Body));
        Assert.Equal("fresh", _recorder.Posts[0].Body["name"]!.GetValue<string>());
        Assert.Null(_recorder.Posts[1].Body["secrets"]);
    }

    [Fact]
    public async Task A_retry_of_a_failed_provision_carries_credentials_for_the_stored_profiles()
    {
        var (engineHost, laptop, engine) = await StartAsync();
        var now = DateTimeOffset.UtcNow;
        var jobs = engineHost.Services.GetRequiredService<IJobStore>();
        await jobs.UpsertAsync(new Job("failed-provision", "provision", "dev", "alice", JobState.Failed, [], null, "boom", now, now, "alice", null), default);
        await jobs.UpsertAsync(new Job("failed-restore", "restore", "dev", "alice", JobState.Failed, [], null, "boom", now, now, "alice", null), default);

        await laptop.PostAsync($"api/engines/{engine}/jobs/failed-provision/retry", null, HttpStatusCode.Accepted);
        await laptop.PostAsync($"api/engines/{engine}/jobs/failed-restore/retry", new { secrets = new { backupKey = "k" } }, HttpStatusCode.Accepted);

        Assert.Equal("/api/v1/jobs/failed-provision/retry", _recorder.Posts[0].Path);
        Assert.Equal(Creds, SentCreds(_recorder.Posts[0].Body));
        Assert.Null(SentCreds(_recorder.Posts[1].Body));
    }

    [Fact]
    public async Task Another_users_vm_gets_none_of_this_devices_credentials()
    {
        var (engineHost, laptop, engine) = await StartAsync();
        var vms = engineHost.Services.GetRequiredService<IVmRepository>();
        var dev = (await vms.GetAsync("dev", default))!;
        Assert.Equal(VmAddOutcome.Added, await vms.AddAsync(dev with { Name = "bobs", Owner = "bob", VmTokenHash = null }, 10, default));

        using var response = await laptop.Api.PostAsJsonAsync($"api/engines/{engine}/vms/bobs/jobs",
            new { kind = "provision", requestId = "b1", desired = new { projects = new[] { "web" }, settings = new { } } });

        var (_, sent) = Assert.Single(_recorder.Posts);
        Assert.Null(sent["secrets"]);
        Assert.Empty(FakeGit.Calls(_bin));
    }

    [Fact]
    public async Task An_engine_refusal_comes_back_unchanged()
    {
        var (_, laptop, engine) = await StartAsync();
        _recorder.Refuse = true;

        using var response = await laptop.Api.PostAsJsonAsync($"api/engines/{engine}/vms/dev/jobs", new { kind = "provision", requestId = "x" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("desired-state-missing", problem["code"]!.GetValue<string>());
        Assert.Equal("job-9", problem["jobId"]!.GetValue<string>());
        Assert.Null(problem["hostsWithoutCredential"]);
    }

    public async ValueTask DisposeAsync()
    {
        if (_laptop is not null) await _laptop.DisposeAsync();
        _engine?.Dispose();
        try { Directory.Delete(_bin, true); }
        catch (IOException) { }
    }

    /// <summary>Answers the engine's job starts itself once <see cref="On"/>, recording each body; everything else reaches the engine.</summary>
    private sealed class JobRecorder : IStartupFilter
    {
        public volatile bool On;
        public volatile bool Refuse;
        private readonly ConcurrentQueue<(string Path, JsonObject Body)> _posts = new();

        public List<(string Path, JsonObject Body)> Posts => [.. _posts];

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                var path = context.Request.Path.Value ?? "";
                var start = context.Request.Method == "POST" && (path == "/api/v1/vms" || path.EndsWith("/jobs", StringComparison.Ordinal) || path.EndsWith("/retry", StringComparison.Ordinal));
                if (!On || !start)
                {
                    await following();
                    return;
                }

                using var reader = new StreamReader(context.Request.Body);
                _posts.Enqueue((path, JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject()));
                context.Response.StatusCode = Refuse ? 409 : 202;
                context.Response.ContentType = Refuse ? "application/problem+json" : "application/json";
                await context.Response.WriteAsync(Refuse
                    ? """{"title":"No settings","status":409,"code":"desired-state-missing","jobId":"job-9"}"""
                    : """{"jobId":"recorded-job","kind":"provision","replayed":false}""");
            });
            next(app);
        };
    }
}
