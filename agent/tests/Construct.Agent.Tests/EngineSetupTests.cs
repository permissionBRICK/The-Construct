using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Construct.Agent.Engines;
using Construct.Agent.Tests.Support;
using Constructd.Core.Domain;

namespace Construct.Agent.Tests;

/// <summary>
/// Setting up the engine on this PC (plan 5.3), as the first run starts it and the upgrade's engine step runs it:
/// the host package of the device's channel checked against its manifest, the installer run elevated and unattended
/// (faked here: it writes what the real one writes), and its three outcomes: done, failed with a reason, restart.
/// </summary>
public sealed class EngineSetupTests : IAsyncDisposable
{
    private const string Manifest = "https://updates.example/construct/manifest.json";
    private readonly LegacyFixture _fixture = new();
    private readonly FakeUpgradePlatform _platform;
    private readonly EngineHost _engine = new();
    private readonly byte[] _package = Package();
    private TestAgent _agent = null!;
    private string _token = "";
    private string? _sha;

    public EngineSetupTests() => _platform = new FakeUpgradePlatform(_fixture);

    private async Task StartAsync()
    {
        _token = await _engine.AddUserAsync("alice", Role.Admin);
        _agent = await TestAgent.StartAsync(localEnginePort: _engine.Port, upgrade: _platform);
        using (var put = await _agent.Api.PutAsJsonAsync("api/device/settings", new { channel = Manifest }))
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        _agent.Host.EngineSetup.Http = new HttpClient(new FakeDevice.StaticHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            Manifest => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject
                {
                    ["releaseTag"] = "nightly-1", ["payloadAsset"] = "construct-host.zip", ["payloadSha256"] = _sha ?? Convert.ToHexStringLower(SHA256.HashData(_package)),
                }.ToJsonString()),
            },
            "https://updates.example/construct/construct-host.zip" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_package) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));
    }

    private static byte[] Package()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry("scripts/service/host/Install-ConstructHost.ps1").Open()))
            writer.Write("# the installer");
        return memory.ToArray();
    }

    private string File_(string name) => Path.Combine(_agent.Directory, name);

    /// <summary>The installer's stand-in: checks how it was started, then writes what the real one writes for <paramref name="result"/>.</summary>
    private void Installer(string result, string message = "", int exit = 0, bool token = true) => _platform.Elevated = (program, arguments) =>
    {
        Assert.Equal("powershell.exe", program);
        Assert.Contains("-NonInteractive", arguments);
        Assert.Equal("Hidden", arguments[arguments.ToList().IndexOf("-WindowStyle") + 1]);
        Assert.DoesNotContain("-File", arguments);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(arguments[^1]));
        Assert.Contains("ResultFile = $p.resultFile", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Read-Host", script, StringComparison.Ordinal);
        var payload = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(
            System.Text.RegularExpressions.Regex.Match(script, @"FromBase64String\('([A-Za-z0-9+/=]+)'\)").Groups[1].Value)))!;
        Assert.Equal(File_(LocalEngineSetup.ResultFileName), payload["resultFile"]!.GetValue<string>());
        Assert.Equal(File_(LocalEngineSetup.LogFileName), payload["log"]!.GetValue<string>());
        Assert.True(File.Exists(payload["zip"]!.GetValue<string>()), "The checked package is there for the elevated part");
        if (token)
            File.WriteAllText(payload["tokenFile"]!.GetValue<string>(),
                new JsonObject { ["url"] = _engine.Url, ["user"] = "alice", ["token"] = _token, ["certificateSha256"] = _engine.Pin }.ToJsonString());
        File.WriteAllText(payload["resultFile"]!.GetValue<string>(), new JsonObject { ["result"] = result, ["message"] = message }.ToJsonString());
        return exit;
    };

    private async Task<JsonNode> SetupAsync()
    {
        await _agent.PostAsync("api/engines/local/setup", null, HttpStatusCode.Accepted);
        await _agent.Host.EngineSetup.Work;
        return await _agent.GetAsync("api/engines/local/setup");
    }

    [Fact]
    public async Task Done_adds_this_PCs_engine_and_the_first_device_is_set_up_against_it()
    {
        await StartAsync();
        Assert.True((await _agent.GetAsync("api/device/features"))["engineHost"]!.GetValue<bool>());
        Assert.Equal("idle", (await _agent.GetAsync("api/engines/local/setup"))["state"]!.GetValue<string>());
        Installer("done");

        var view = await SetupAsync();
        Assert.Equal("done", view["state"]!.GetValue<string>());
        var engine = view["engine"]!.GetValue<string>();
        var added = (await _agent.GetAsync("api/state"))["engines"]!.AsArray().Single()!;
        Assert.Equal(engine, added["id"]!.GetValue<string>());
        Assert.Equal("This PC", added["name"]!.GetValue<string>());
        Assert.Equal(_engine.Url, added["url"]!.GetValue<string>());
        Assert.Single(_platform.ElevatedRuns);
        // The token went into the state, protected; nothing is left of it, or of the package, on disk.
        Assert.False(File.Exists(File_(LocalEngineSetup.TokenFileName)));
        Assert.False(File.Exists(File_("engine-package.zip")));
        Assert.DoesNotContain(_token, _agent.StateFileText(), StringComparison.Ordinal);
        Assert.DoesNotContain(_platform.ElevatedRuns.Single(), a => a.Contains(_token, StringComparison.Ordinal));

        // Then the device name and the code, against that engine, with nothing typed between.
        var setup = await _agent.PostAsync("api/device/setup", new { engine, deviceName = "pc" });
        Assert.False(string.IsNullOrEmpty(setup["recoveryCode"]?.GetValue<string>()));

        // A second start takes the engine it holds, without another prompt.
        Assert.Equal(engine, (await SetupAsync())["engine"]!.GetValue<string>());
        Assert.Single(_platform.ElevatedRuns);
    }

    [Fact]
    public async Task Failed_shows_the_installers_reason_and_adds_nothing()
    {
        await StartAsync();
        Installer("failed", "Hyper-V could not be enabled on this Windows Home edition.", exit: 1, token: false);

        var view = await SetupAsync();
        Assert.Equal("failed", view["state"]!.GetValue<string>());
        Assert.Equal("Hyper-V could not be enabled on this Windows Home edition.", view["message"]!.GetValue<string>());
        Assert.Empty((await _agent.GetAsync("api/state"))["engines"]!.AsArray());
        await _agent.PostAsync("api/device/restart", null, HttpStatusCode.Conflict);
        Assert.Equal(0, _platform.Restarts);

        // Retry, and this time it goes through.
        Installer("done");
        Assert.Equal("done", (await SetupAsync())["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Restart_needed_is_reported_and_Windows_restarts_only_on_the_users_click()
    {
        await StartAsync();
        await _agent.PostAsync("api/device/restart", null, HttpStatusCode.Conflict);
        Installer("restart", "Windows must restart to turn on Hyper-V.", exit: 3, token: false);

        var view = await SetupAsync();
        Assert.Equal("restart", view["state"]!.GetValue<string>());
        Assert.Equal("Windows must restart to turn on Hyper-V.", view["message"]!.GetValue<string>());
        Assert.Equal(0, _platform.Restarts);
        Assert.Empty((await _agent.GetAsync("api/state"))["engines"]!.AsArray());

        await _agent.PostAsync("api/device/restart", null, HttpStatusCode.Accepted);
        Assert.Equal(1, _platform.Restarts);
    }

    [Fact]
    public async Task No_result_names_the_exit_code_and_the_log()
    {
        await StartAsync();
        _platform.Elevated = (_, _) => 1;
        var view = await SetupAsync();
        Assert.Equal("failed", view["state"]!.GetValue<string>());
        Assert.Contains("exit code 1", view["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("engine-install.log", view["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_package_that_does_not_match_the_manifest_is_never_run()
    {
        await StartAsync();
        _sha = new string('0', 64);
        Installer("done");
        var view = await SetupAsync();
        Assert.Equal("failed", view["state"]!.GetValue<string>());
        Assert.Contains("SHA-256", view["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(_platform.ElevatedRuns);
        Assert.False(File.Exists(File_("engine-package.zip")));
    }

    [Fact]
    public async Task A_token_file_naming_another_machine_is_refused()
    {
        await StartAsync();
        _agent.Host.EngineSetup.Installer = _ =>
        {
            var file = File_(LocalEngineSetup.TokenFileName);
            File.WriteAllText(file, new JsonObject { ["url"] = "https://elsewhere.example:7462", ["token"] = _token, ["certificateSha256"] = _engine.Pin }.ToJsonString());
            return Task.FromResult(file);
        };
        var view = await SetupAsync();
        Assert.Equal("failed", view["state"]!.GetValue<string>());
        Assert.Empty((await _agent.GetAsync("api/state"))["engines"]!.AsArray());
        Assert.False(File.Exists(File_(LocalEngineSetup.TokenFileName)));
    }

    [Fact]
    public async Task A_device_that_cannot_host_an_engine_is_not_offered_one()
    {
        _platform.CanHostEngine = false;
        await StartAsync();
        Assert.False((await _agent.GetAsync("api/device/features"))["engineHost"]!.GetValue<bool>());
        Assert.False((await _agent.GetAsync("api/engines/local/setup"))["available"]!.GetValue<bool>());
        await _agent.PostAsync("api/engines/local/setup", null, HttpStatusCode.NotImplemented);
    }

    public async ValueTask DisposeAsync()
    {
        if (_agent is not null) await _agent.DisposeAsync();
        _engine.Dispose();
        _fixture.Dispose();
    }
}
