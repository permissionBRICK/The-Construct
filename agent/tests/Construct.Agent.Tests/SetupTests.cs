using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Construct.Agent.Setup;

namespace Construct.Agent.Tests;

/// <summary>
/// ConstructSetup.exe's install logic against a fake Windows and a fake channel: the folders, install.json, device.json,
/// the Run values, the VS Code extension, the old Companion asked to quit (and started again when the setup fails), and
/// the hand-over to an agent that runs already. The Windows seam itself (registry, processes) only builds here.
/// </summary>
public sealed class SetupTests : IDisposable
{
    private const string Version = "1.2.0+2222222222222222222222222222222222222222";
    private const string Tag = "host-2222222222222222222222222222222222222222";
    private const string Asset = "construct-agent-2222222-win-x64.zip";
    private const string Repository = "owner/repo";
    private const string NightlyManifest = "https://github.com/owner/repo/releases/download/channel-nightly/manifest.json";
    private const string StableManifest = "https://github.com/owner/repo/releases/latest/download/manifest.json";
    private const string PackageUrl = "https://github.com/owner/repo/releases/download/" + Tag + "/" + Asset;
    private const string EngineAsset = "construct-host-2222222-win-x64.zip";
    private const string EngineUrl = "https://github.com/owner/repo/releases/download/" + Tag + "/" + EngineAsset;
    private const int AgentPort = 47001;
    private const int CompanionPort = 47002;
    private const int CompanionPid = 4242;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-test-setup-" + Guid.NewGuid().ToString("n"));
    private readonly byte[] _package = Package();
    private readonly byte[] _engine = "the host package"u8.ToArray();
    private string _engineSha = Convert.ToHexStringLower(SHA256.HashData("the host package"u8.ToArray()));
    private readonly FakeWindows _windows;
    private readonly Channel _web;
    private readonly StringWriter _output = new();
    private string _sha;

    public SetupTests()
    {
        _windows = new FakeWindows(Path.Combine(_root, "LocalAppData"));
        _sha = Convert.ToHexStringLower(SHA256.HashData(_package));
        _web = new Channel(this);
    }

    public void Dispose()
    {
        _web.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string AgentHome => _windows.AgentHome;

    private string Installed => Path.Combine(AgentHome, "app", AgentSetup.AgentExecutable);

    private AgentSetup Setup() => new(_windows, _web.Client, _web.Client, _output)
    {
        Repository = Repository,
        Poll = TimeSpan.FromMilliseconds(5),
        AgentStartLimit = TimeSpan.FromSeconds(2),
        CompanionQuitLimit = TimeSpan.FromMilliseconds(300),
    };

    private JsonElement ReadJson(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(AgentHome, name))).RootElement;

    [Fact]
    public async Task A_fresh_install_unpacks_the_checked_package_records_it_starts_it_with_windows_and_opens_its_window()
    {
        _windows.VsCode = "/vscode/bin/code.cmd";

        var outcome = await Setup().RunAsync("nightly");

        Assert.Equal(new SetupOutcome(Version, HandedOver: false), outcome);
        Assert.Equal([NightlyManifest, PackageUrl], _web.Requests.Where(r => r.Url.StartsWith("https://", StringComparison.Ordinal)).Select(r => r.Url));
        // The package as it was published, in app\ (the one folder the agent runs from), with its version.
        Assert.Equal("agent", File.ReadAllText(Installed));
        Assert.True(File.Exists(Path.Combine(AgentHome, "app", "ui", "index.html")));
        Assert.Equal(Version, File.ReadAllText(Path.Combine(AgentHome, "app", ".version")));
        Assert.False(Directory.Exists(Path.Combine(AgentHome, "app.incoming")));
        var state = ReadJson("install.json");
        Assert.Equal((Version, JsonValueKind.Null, false), (state.GetProperty("current").GetString(), state.GetProperty("previous").ValueKind, state.GetProperty("pending").GetBoolean()));
        Assert.Equal("nightly", ReadJson("device.json").GetProperty("channel").GetString());
        Assert.False(File.ReadAllBytes(Path.Combine(AgentHome, "install.json")).AsSpan().StartsWith(Encoding.UTF8.Preamble), "no BOM");
        // The extension it carries went into VS Code, before the agent started.
        Assert.Equal([("/vscode/bin/code.cmd", $"--install-extension {Path.Combine(AgentHome, "app.incoming", "construct.vsix")} --force")], _windows.Ran);
        // Starts with Windows, in the background, and was started the way a user starts it.
        Assert.Equal($"\"{Installed}\" --background", _windows.RunValues[AgentSetup.AgentRunValue]);
        // Started the way a user starts it, which opens its window; a just started agent answers its API before its window
        // exists and would refuse a request for it.
        Assert.Equal([(Installed, "")], _windows.Started);
        Assert.DoesNotContain(_web.Requests, r => r.Url.EndsWith("/api/shell/open", StringComparison.Ordinal));
        Assert.False(_windows.RunValues.ContainsKey(AgentSetup.CompanionRunValue));
        Assert.Contains("Its window finishes the setup", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Over_an_installed_agent_that_is_not_running_the_new_version_is_on_trial_and_the_agents_settings_stay()
    {
        Directory.CreateDirectory(AgentHome);
        File.WriteAllText(Path.Combine(AgentHome, "install.json"), "﻿{\"current\":\"1.0.0+aaaa\",\"previous\":null,\"pending\":false}");
        File.WriteAllText(Path.Combine(AgentHome, "device.json"), """{"channel":"stable","autoUpdate":true}""");
        // A stale token file of an agent that ended without removing it: nothing answers there.
        File.WriteAllText(Path.Combine(AgentHome, "endpoint.json"), $$"""{"port":{{AgentPort}},"token":"stale","pid":1}""");

        await Setup().RunAsync("nightly");

        var state = ReadJson("install.json");
        Assert.Equal(("1.0.0+aaaa", true), (state.GetProperty("previous").GetString(), state.GetProperty("pending").GetBoolean()));
        Assert.Equal("""{"channel":"stable","autoUpdate":true}""", File.ReadAllText(Path.Combine(AgentHome, "device.json")));
        // No VS Code here: no failure.
        Assert.Empty(_windows.Ran);
        Assert.Contains("VS Code is not installed", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Over_an_agent_in_app_the_replaced_version_waits_in_previous_for_the_way_back()
    {
        Directory.CreateDirectory(Path.Combine(AgentHome, "app"));
        File.WriteAllText(Installed, "old agent");
        File.WriteAllText(Path.Combine(AgentHome, "app", ".version"), "1.0.0+aaaa");
        File.WriteAllText(Path.Combine(AgentHome, "install.json"), """{"current":"1.0.0+aaaa","previous":null,"pending":false}""");

        await Setup().RunAsync("nightly");

        Assert.Equal("agent", File.ReadAllText(Installed));
        Assert.Equal("old agent", File.ReadAllText(Path.Combine(AgentHome, "previous", AgentSetup.AgentExecutable)));
        Assert.Equal(("1.0.0+aaaa", true), (ReadJson("install.json").GetProperty("previous").GetString(), ReadJson("install.json").GetProperty("pending").GetBoolean()));
    }

    [Fact]
    public async Task The_same_version_again_keeps_its_folder_and_is_not_downloaded_twice()
    {
        await Setup().RunAsync("stable");
        // The agent quit (it removes its token file).
        File.Delete(Path.Combine(AgentHome, "endpoint.json"));
        _web.Requests.Clear();

        await Setup().RunAsync("stable");

        Assert.Equal([StableManifest], _web.Requests.Where(r => r.Url.StartsWith("https://", StringComparison.Ordinal)).Select(r => r.Url));
        var state = ReadJson("install.json");
        Assert.Equal((Version, JsonValueKind.Null, false), (state.GetProperty("current").GetString(), state.GetProperty("previous").ValueKind, state.GetProperty("pending").GetBoolean()));
        Assert.Equal("stable", ReadJson("device.json").GetProperty("channel").GetString());
    }

    [Fact]
    public async Task A_package_that_does_not_match_the_manifests_hash_installs_nothing()
    {
        _sha = new string('0', 64);

        var failure = await Assert.ThrowsAsync<SetupFailure>(() => Setup().RunAsync("nightly"));

        Assert.Contains("does not match the SHA-256", failure.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(AgentHome, "app")));
        Assert.False(File.Exists(Path.Combine(AgentHome, "install.json")));
        Assert.Empty(_windows.RunValues);
        Assert.Empty(_windows.Started);
    }

    [Theory]
    [InlineData("""{"version":"1.2.0+x","releaseTag":"host-x","packages":{"agentLinux":{"asset":"a.zip","sha256":"%"}}}""")]
    [InlineData("""{"version":"1.2.0+x","releaseTag":"host-x","packages":{"agentWindows":{"asset":"../a.zip","sha256":"%"}}}""")]
    [InlineData("""{"version":"1.2.0+x","releaseTag":"host-x","packages":{"agentWindows":{"asset":"a.zip","sha256":"abc"}}}""")]
    [InlineData("""{"version":"..","releaseTag":"host-x","packages":{"agentWindows":{"asset":"a.zip","sha256":"%"}}}""")]
    [InlineData("""{"version":"1.2.0 x","releaseTag":"host-x","packages":{"agentWindows":{"asset":"a.zip","sha256":"%"}}}""")]
    [InlineData("""{"version":"1.2.0+x","releaseTag":"host/x","packages":{"agentWindows":{"asset":"a.zip","sha256":"%"}}}""")]
    [InlineData("""not json""")]
    public async Task A_manifest_without_a_usable_windows_agent_is_refused(string manifest)
    {
        _web.Manifest = manifest.Replace("%", new string('a', 64), StringComparison.Ordinal);

        var failure = await Assert.ThrowsAsync<SetupFailure>(() => Setup().RunAsync("nightly"));

        Assert.Equal("The nightly channel offers no Construct agent for Windows.", failure.Message);
        Assert.False(Directory.Exists(Path.Combine(AgentHome, "app")));
    }

    [Fact]
    public void Channels_resolve_as_the_agents_updater_resolves_them()
    {
        Assert.Equal(new Uri(StableManifest), SetupChannel.ManifestUrl("stable", Repository));
        Assert.Equal(new Uri(NightlyManifest), SetupChannel.ManifestUrl("nightly", Repository));
        Assert.Equal(new Uri("https://mirror.example/c/manifest.json"), SetupChannel.ManifestUrl("https://mirror.example/c/manifest.json", Repository));
        Assert.Null(SetupChannel.ManifestUrl("http://mirror.example/c/manifest.json", Repository));
        Assert.Null(SetupChannel.ManifestUrl("beta", Repository));
        Assert.Null(SetupChannel.ManifestUrl("stable", "owner/repo/../x"));
        // A mirror's package lies beside its manifest.
        var sha = new string('A', 64);
        var package = SetupChannel.Package($$"""{"version":"1.0.0+x","packages":{"agentWindows":{"asset":"agent win.zip","sha256":"{{sha}}"} } }""",
            new Uri("https://mirror.example/c/manifest.json"), "https://mirror.example/c/manifest.json", Repository);
        Assert.Equal((new Uri("https://mirror.example/c/agent%20win.zip"), sha.ToLowerInvariant()), (package.Url, package.Sha256));
    }

    [Fact]
    public async Task An_unknown_channel_is_refused_before_anything_happens()
    {
        await Assert.ThrowsAsync<SetupFailure>(() => Setup().RunAsync("beta"));
        Assert.Empty(_web.Requests);
    }

    // ---- an agent that runs already ----

    [Fact]
    public async Task A_running_agent_is_handed_over_to_it_opens_its_window_and_installs_its_update()
    {
        await AgentRunning("1.0.0+aaaa", vsix: true);
        _windows.VsCode = "/vscode/bin/code.cmd";
        _web.UpdateOffer = "1.2.0+bbbb";

        var outcome = await Setup().RunAsync("nightly");

        Assert.Equal(new SetupOutcome(null, HandedOver: true), outcome);
        // Nothing was downloaded or written by the setup: the agent updates itself on its own channel.
        Assert.DoesNotContain(_web.Requests, r => r.Url.StartsWith("https://", StringComparison.Ordinal));
        Assert.Equal(
            [$"GET http://127.0.0.1:{AgentPort}/api/health", $"POST http://127.0.0.1:{AgentPort}/api/shell/open", $"POST http://127.0.0.1:{AgentPort}/api/update/check", $"POST http://127.0.0.1:{AgentPort}/api/update/install"],
            _web.Requests.Select(r => r.Method + " " + r.Url));
        Assert.All(_web.Requests.Where(r => r.Method == "POST"), r => Assert.Equal("Bearer agent-token", r.Authorization));
        Assert.Equal([("/vscode/bin/code.cmd", $"--install-extension {Path.Combine(AgentHome, "versions", "1.0.0+aaaa", "construct.vsix")} --force")], _windows.Ran);
        Assert.Empty(_windows.Started);
        Assert.Empty(_windows.RunValues);
        Assert.Equal("""{"current":"1.0.0+aaaa"}""", File.ReadAllText(Path.Combine(AgentHome, "install.json")));
    }

    [Fact]
    public async Task A_running_agent_without_an_update_only_opens_its_window()
    {
        await AgentRunning("1.0.0+aaaa", vsix: false);
        _windows.VsCode = "/vscode/bin/code.cmd";

        await Setup().RunAsync("stable");

        Assert.DoesNotContain(_web.Requests, r => r.Url.EndsWith("/api/update/install", StringComparison.Ordinal));
        Assert.Contains(_web.Requests, r => r.Url.EndsWith("/api/shell/open", StringComparison.Ordinal));
        Assert.Empty(_windows.Ran);
    }

    [Fact]
    public async Task A_running_agent_that_cannot_check_still_counts_as_handed_over()
    {
        await AgentRunning("1.0.0+aaaa", vsix: false);
        _web.UpdateCheckFails = true;

        var outcome = await Setup().RunAsync("stable");

        Assert.True(outcome.HandedOver);
        Assert.Contains("checks by itself every six hours", _output.ToString(), StringComparison.Ordinal);
    }

    // ---- the engine of a move (R18) ----

    private void OldInstall(string registry, bool defaultKey = false)
    {
        var root = Path.Combine(_windows.LocalAppData, "The-Construct");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "instances.json"), registry);
        var key = Path.Combine(_windows.Home, ".ssh", "agent_vm_ed25519");
        if (defaultKey)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(key)!);
            File.WriteAllText(key, "key");
        }
        else if (File.Exists(key))
        {
            File.Delete(key);
        }
    }

    [Fact]
    public async Task A_move_from_an_old_install_with_a_local_vm_downloads_the_engine_in_the_console_before_the_agent_starts()
    {
        OldInstall("""{"version":1,"instances":{"agent-vm":{"backend":"hyperv-local","vmName":"Agent-VM"},"work":{"backend":"hyperv-remote"}}}""", defaultKey: true);

        await Setup().RunAsync("nightly");

        Assert.Equal(_engine, File.ReadAllBytes(Path.Combine(AgentHome, AgentSetup.EnginePackageFile)));
        Assert.False(File.Exists(Path.Combine(AgentHome, AgentSetup.EnginePackageFile + ".partial")));
        // Before the window opens: the agent is started after it.
        Assert.Equal([NightlyManifest, PackageUrl, EngineUrl], _web.Requests.Where(r => r.Url.StartsWith("https://", StringComparison.Ordinal)).Select(r => r.Url));
        Assert.Single(_windows.Started);
    }

    [Fact]
    public async Task A_move_of_remote_vms_only_or_a_fresh_install_downloads_no_engine()
    {
        await Setup().RunAsync("nightly");
        Assert.DoesNotContain(_web.Requests, r => r.Url == EngineUrl);
        Assert.Equal("fresh", JsonDocument.Parse(File.ReadAllText(Path.Combine(AgentHome, "bridge.json"))).RootElement.GetProperty("stage").GetString());

        File.Delete(Path.Combine(AgentHome, "bridge.json"));
        OldInstall("""{"version":1,"instances":{"work":{"backend":"hyperv-remote"}}}""");
        await Setup().RunAsync("nightly");
        Assert.DoesNotContain(_web.Requests, r => r.Url == EngineUrl);
        Assert.False(File.Exists(Path.Combine(AgentHome, AgentSetup.EnginePackageFile)));
        Assert.False(File.Exists(Path.Combine(AgentHome, "bridge.json")));

        // Save-ConstructInstances materialized the default local agent-vm on remote-only PCs without creating its key.
        OldInstall("""{"version":1,"instances":{"agent-vm":{"backend":"hyperv-local","vmName":"Agent-VM"},"work":{"backend":"hyperv-remote"}}}""");
        await Setup().RunAsync("nightly");
        Assert.DoesNotContain(_web.Requests, r => r.Url == EngineUrl);
        Assert.False(File.Exists(Path.Combine(AgentHome, AgentSetup.EnginePackageFile)));
    }

    [Fact]
    public async Task An_engine_download_that_fails_its_check_leaves_nothing_and_the_setup_goes_on()
    {
        OldInstall("""{"version":1,"instances":{"agent-vm":{"backend":"hyperv-local"}}}""", defaultKey: true);
        _engineSha = new string('0', 64);

        var outcome = await Setup().RunAsync("nightly");

        Assert.False(outcome.HandedOver);
        Assert.False(File.Exists(Path.Combine(AgentHome, AgentSetup.EnginePackageFile)));
        Assert.False(File.Exists(Path.Combine(AgentHome, AgentSetup.EnginePackageFile + ".partial")));
        Assert.Contains("downloaded when it is installed", _output.ToString(), StringComparison.Ordinal);
        Assert.Single(_windows.Started);
    }

    [Fact]
    public void Whether_the_old_install_runs_a_vm_here_follows_the_converters_defaults()
    {
        bool RunsVm(string registry, bool defaultKey = false)
        {
            OldInstall(registry, defaultKey);
            return Setup().OldInstallRunsVmHere();
        }

        const string Remote = "\"work\":{\"backend\":\"hyperv-remote\",\"service\":{\"url\":\"https://host.example\"}}";
        // Materialized default agent-vm without its SSH key was written by Save-ConstructInstances on remote-only PCs.
        Assert.False(RunsVm("""{"version":1,"instances":{"agent-vm":{}}}"""));
        Assert.True(RunsVm("""{"version":1,"instances":{"agent-vm":{}}}""", defaultKey: true));
        Assert.False(RunsVm("{\"version\":1,\"instances\":{\"agent-vm\":{\"backend\":\"hyperv-local\"}," + Remote + "}}"));
        Assert.True(RunsVm("{\"version\":1,\"instances\":{\"agent-vm\":{\"backend\":\"hyperv-local\"}," + Remote + "}}", defaultKey: true));
        Assert.True(RunsVm("""{"version":1,"instances":{"box":{"backend":"HyperV-Local"}}}"""));
        Assert.False(RunsVm("""{"version":1}"""));                                       // no instances: only with default key
        Assert.True(RunsVm("""{"version":1}""", defaultKey: true));
        Assert.False(RunsVm("{\"version\":1,\"instances\":{" + Remote + "}}"));
        Assert.True(RunsVm("{\"version\":1,\"instances\":{" + Remote + "}}", defaultKey: true)); // implicit default VM
        Assert.False(RunsVm("{\"version\":1,\"instances\":{\"agent-vm\":null," + Remote + "}}", defaultKey: true)); // removed
        Assert.False(RunsVm("""{"version":1,"instances":{"construct-box":{"backend":"hyperv-local"},"Box":{}}}"""));

        // Without instances.json, legacy files alone do not imply a local VM unless the default key exists.
        File.Delete(Path.Combine(_windows.LocalAppData, "The-Construct", "instances.json"));
        var installFolder = Path.Combine(_windows.LocalAppData, "The-Construct", "repo-main", "construct-main");
        Directory.CreateDirectory(installFolder);
        File.WriteAllText(Path.Combine(installFolder, ".construct-settings.json"), "{}");
        var key = Path.Combine(_windows.Home, ".ssh", "agent_vm_ed25519");
        if (File.Exists(key)) File.Delete(key);
        Assert.False(Setup().OldInstallRunsVmHere());
        Directory.CreateDirectory(Path.GetDirectoryName(key)!);
        File.WriteAllText(key, "key");
        Assert.True(Setup().OldInstallRunsVmHere());
    }

    [Fact]
    public async Task A_cached_engine_package_that_cannot_be_read_does_not_stop_the_setup()
    {
        OldInstall("""{"version":1,"instances":{"agent-vm":{}}}""", defaultKey: true);
        Directory.CreateDirectory(AgentHome);
        var cached = Path.Combine(AgentHome, AgentSetup.EnginePackageFile);
        File.WriteAllText(cached, "an older package");

        SetupOutcome outcome;
        using (new FileStream(cached, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            outcome = await Setup().RunAsync("nightly");

        Assert.False(outcome.HandedOver);
        Assert.Contains("downloaded when it is installed", _output.ToString(), StringComparison.Ordinal);
        Assert.Single(_windows.Started);
    }

    // ---- the old Companion ----

    [Fact]
    public async Task An_old_companion_is_asked_to_quit_its_own_way_and_no_longer_starts_with_windows()
    {
        Companion(running: true);
        _windows.RunValues[AgentSetup.CompanionRunValue] = "ConstructCompanion.exe --background";

        await Setup().RunAsync("stable");

        var quit = Assert.Single(_web.Requests, r => r.Url.EndsWith("/v1/quit", StringComparison.Ordinal));
        Assert.Equal(($"http://127.0.0.1:{CompanionPort}/v1/quit", "POST", "Bearer companion-token", """{"reason":"update"}"""), (quit.Url, quit.Method, quit.Authorization, quit.Body));
        // It quit before the agent was started, and stays off: the agent holds its mutex now.
        Assert.True(_web.Requests.IndexOf(quit) < _web.Requests.FindIndex(r => r.Url == PackageUrl));
        Assert.False(_windows.RunValues.ContainsKey(AgentSetup.CompanionRunValue));
        Assert.Equal([Installed], _windows.Started.Select(s => s.Executable));
    }

    [Fact]
    public async Task A_companion_its_endpoint_does_not_name_stops_the_setup_before_anything_quits()
    {
        Companion(running: true);
        _windows.Running.Add(9999);
        _windows.Session.Add(9999);

        var failure = await Assert.ThrowsAsync<SetupFailure>(() => Setup().RunAsync("stable"));

        Assert.Equal("The Construct Companion is still running; close it from its tray menu and try again.", failure.Message);
        Assert.DoesNotContain(_web.Requests, r => r.Url.EndsWith("/v1/quit", StringComparison.Ordinal) || r.Url.StartsWith("https://", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(AgentHome, "install.json")));
    }

    [Fact]
    public async Task A_companion_that_does_not_quit_fails_the_setup_and_is_never_killed()
    {
        Companion(running: true);
        _web.CompanionIgnoresQuit = true;

        var failure = await Assert.ThrowsAsync<SetupFailure>(() => Setup().RunAsync("stable"));

        Assert.Equal("The Construct Companion did not quit; close it from its tray menu and try again.", failure.Message);
        Assert.Contains(CompanionPid, _windows.Running);
        Assert.Empty(_windows.Started);
        Assert.False(File.Exists(Path.Combine(AgentHome, "install.json")));
    }

    [Fact]
    public async Task A_setup_cancelled_while_the_companion_quits_waits_for_it_and_starts_it_again()
    {
        Companion(running: true);
        var companionExe = Path.Combine(_windows.LocalAppData, "Programs", "ConstructCompanion", "ConstructCompanion.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(companionExe)!);
        File.WriteAllText(companionExe, "companion");
        using var cancel = new CancellationTokenSource();
        // Ctrl+C right after the Companion accepted the quit; it exits a moment later.
        _web.CompanionIgnoresQuit = true;
        _web.OnQuit = () =>
        {
            cancel.Cancel();
            _ = Task.Delay(100).ContinueWith(_ => { lock (_windows.Running) _windows.Running.Remove(CompanionPid); });
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Setup().RunAsync("stable", cancel.Token));

        Assert.Equal([(companionExe, "--background")], _windows.Started);
        Assert.False(File.Exists(Path.Combine(AgentHome, "install.json")));
    }

    [Fact]
    public async Task A_failure_after_the_companion_quit_starts_it_again_and_keeps_it_starting_with_windows()
    {
        Companion(running: true);
        _windows.RunValues[AgentSetup.CompanionRunValue] = "ConstructCompanion.exe --background";
        var companionExe = Path.Combine(_windows.LocalAppData, "Programs", "ConstructCompanion", "ConstructCompanion.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(companionExe)!);
        File.WriteAllText(companionExe, "companion");
        _windows.VsCode = "/vscode/bin/code.cmd";
        _windows.VsCodeExit = 1;

        var failure = await Assert.ThrowsAsync<SetupFailure>(() => Setup().RunAsync("stable"));

        Assert.Equal("VS Code did not install the Construct extension (code --install-extension exit 1).", failure.Message);
        Assert.Equal([(companionExe, "--background")], _windows.Started);
        Assert.True(_windows.RunValues.ContainsKey(AgentSetup.CompanionRunValue));
        Assert.False(_windows.RunValues.ContainsKey(AgentSetup.AgentRunValue));
        Assert.False(File.Exists(Path.Combine(AgentHome, "install.json")));
    }

    [Fact]
    public async Task An_agent_that_never_answers_fails_the_setup_and_starts_the_companion_again()
    {
        Companion(running: true);
        var companionExe = Path.Combine(_windows.LocalAppData, "Programs", "ConstructCompanion", "ConstructCompanion.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(companionExe)!);
        File.WriteAllText(companionExe, "companion");
        _windows.AgentNeverStarts = true;

        var failure = await Assert.ThrowsAsync<SetupFailure>(() => Setup().RunAsync("stable"));

        Assert.StartsWith("The Construct agent did not start", failure.Message, StringComparison.Ordinal);
        Assert.Equal([Installed, companionExe], _windows.Started.Select(s => s.Executable));
    }

    [Fact]
    public async Task Without_a_companion_endpoint_any_companion_of_this_session_stops_the_setup()
    {
        _windows.Running.Add(CompanionPid);
        _windows.Session.Add(CompanionPid);

        var failure = await Assert.ThrowsAsync<SetupFailure>(() => Setup().RunAsync("stable"));

        Assert.StartsWith("The Construct Companion is still running", failure.Message, StringComparison.Ordinal);
    }

    // ---- VS Code ----

    [Fact]
    public void Vs_code_is_found_on_the_search_path_first_then_in_its_usual_places()
    {
        var bin = Path.Combine(_root, "path-bin");
        var user = Path.Combine(_root, "user");
        var machine = Path.Combine(_root, "machine");
        Directory.CreateDirectory(bin);
        Assert.Null(VsCode.Find(bin, user, machine));
        var machineCode = Path.Combine(machine, "Microsoft VS Code", "bin", "code.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(machineCode)!);
        File.WriteAllText(machineCode, "");
        Assert.Equal(machineCode, VsCode.Find(bin, user, machine));
        var userCode = Path.Combine(user, "Programs", "Microsoft VS Code", "bin", "code.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(userCode)!);
        File.WriteAllText(userCode, "");
        Assert.Equal(userCode, VsCode.Find(bin, user, machine));
        File.WriteAllText(Path.Combine(bin, "code.cmd"), "");
        Assert.Equal(Path.Combine(bin, "code.cmd"), VsCode.Find("\"" + bin + "\"" + Path.PathSeparator, user, machine));
    }

    // ---- fixtures ----

    private async Task AgentRunning(string current, bool vsix)
    {
        Directory.CreateDirectory(Path.Combine(AgentHome, "versions", current));
        if (vsix) await File.WriteAllTextAsync(Path.Combine(AgentHome, "versions", current, "construct.vsix"), "vsix");
        await File.WriteAllTextAsync(Path.Combine(AgentHome, "install.json"), $$"""{"current":"{{current}}"}""");
        _windows.WriteEndpoint();
    }

    private void Companion(bool running)
    {
        var state = Path.Combine(_windows.LocalAppData, "The-Construct", "companion");
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "endpoint.json"), $$"""{"v":1,"port":{{CompanionPort}},"token":"companion-token","pid":{{CompanionPid}},"startedAt":"2026-11-01T00:00:00Z"}""");
        if (running)
        {
            _windows.Running.Add(CompanionPid);
            _windows.Session.Add(CompanionPid);
        }
    }

    private static byte[] Package()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[] { ("construct-agent.exe", "agent"), ("construct.vsix", "vsix"), ("ui/index.html", "<!doctype html>") })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Windows as the setup sees it: folders, the Run key, processes; starting the agent writes its token file.</summary>
    private sealed class FakeWindows(string localAppData) : ISetupPlatform
    {
        public string AgentHome { get; } = Path.Combine(localAppData, "Construct", "agent");

        public string LocalAppData { get; } = localAppData;

        public string Home { get; } = Path.Combine(Path.GetDirectoryName(localAppData)!, "Home");

        public Dictionary<string, string> RunValues { get; } = [];

        public List<(string Executable, string Argument)> Started { get; } = [];

        public List<(string Program, string Arguments)> Ran { get; } = [];

        public HashSet<int> Running { get; } = [];

        public List<int> Session { get; } = [];

        public string? VsCode { get; set; }

        public int VsCodeExit { get; set; }

        public bool AgentNeverStarts { get; set; }

        public void WriteEndpoint()
        {
            Directory.CreateDirectory(AgentHome);
            File.WriteAllText(Path.Combine(AgentHome, "endpoint.json"),
                $$"""{"version":1,"port":{{AgentPort}},"token":"agent-token","pid":77,"startedAt":"2026-11-21T00:00:00Z","agentVersion":"x"}""");
        }

        public void SetRunValue(string name, string? command)
        {
            if (command is null) RunValues.Remove(name);
            else RunValues[name] = command;
        }

        public void Start(string executable, string argument)
        {
            Started.Add((executable, argument));
            if (executable.EndsWith(AgentSetup.AgentExecutable, StringComparison.Ordinal) && !AgentNeverStarts) WriteEndpoint();
        }

        public bool IsRunning(int pid) { lock (Running) return Running.Contains(pid); }

        /// <summary>The Companions of this session that still run.</summary>
        public IReadOnlyList<int> SessionProcesses(string name) => name == AgentSetup.CompanionProcess ? Session.Where(Running.Contains).ToList() : [];

        public string? FindVsCode() => VsCode;

        public Task<int> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan limit, CancellationToken ct)
        {
            Ran.Add((program, string.Join(' ', arguments)));
            return Task.FromResult(VsCodeExit);
        }
    }

    private sealed record Seen(string Method, string Url, string? Authorization, string? Body);

    /// <summary>GitHub's channel, the running agent's loopback API and the old Companion's, in one handler.</summary>
    private sealed class Channel(SetupTests test) : HttpMessageHandler
    {
        public HttpClient Client => field ??= new HttpClient(this, disposeHandler: false);

        public List<Seen> Requests { get; } = [];

        public string? Manifest { get; set; }

        public string? UpdateOffer { get; set; }

        public bool UpdateCheckFails { get; set; }

        public bool CompanionIgnoresQuit { get; set; }

        /// <summary>Called when the Companion is asked to quit.</summary>
        public Action? OnQuit { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add(new(request.Method.Method, url, request.Headers.Authorization?.ToString(), body));
            var agent = $"http://127.0.0.1:{AgentPort}/api/";
            if (url == NightlyManifest || url == StableManifest)
            {
                return Json(Manifest ?? $$"""
                    {"channel":"nightly","version":"{{Version}}","releaseTag":"{{Tag}}","payloadAsset":"{{EngineAsset}}","payloadSha256":"{{test._engineSha}}",
                     "packages":{"agentLinux":{"asset":"construct-agent-2222222-linux-x64.zip","sha256":"{{new string('b', 64)}}"},
                                 "agentWindows":{"asset":"{{Asset}}","sha256":"{{test._sha}}"} } }
                    """);
            }

            if (url == PackageUrl) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(test._package) };
            if (url == EngineUrl) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(test._engine) };
            if (url.StartsWith(agent, StringComparison.Ordinal))
            {
                // The agent answers once it has written its token file (a stale one names nobody).
                var endpoint = Path.Combine(test.AgentHome, "endpoint.json");
                if (!File.Exists(endpoint) || !File.ReadAllText(endpoint).Contains("agent-token", StringComparison.Ordinal)) throw new HttpRequestException("Connection refused");
                var route = url[agent.Length..];
                if (route == "health") return Json("""{"status":"ok","version":"x","pid":77}""");
                if (request.Headers.Authorization?.ToString() != "Bearer agent-token") return new(HttpStatusCode.Unauthorized);
                return route switch
                {
                    "shell/open" => new(HttpStatusCode.NoContent),
                    "update/check" when UpdateCheckFails => new(HttpStatusCode.BadGateway),
                    "update/check" => Json(UpdateOffer is null ? """{"available":null,"state":"idle"}""" : $$"""{"available":"{{UpdateOffer}}","state":"idle"}"""),
                    "update/install" => new(HttpStatusCode.Accepted) { Content = new StringContent("""{"state":"waiting"}""") },
                    _ => new(HttpStatusCode.NotFound),
                };
            }

            if (url == $"http://127.0.0.1:{CompanionPort}/v1/quit")
            {
                if (request.Headers.Authorization?.ToString() != "Bearer companion-token") return new(HttpStatusCode.Unauthorized);
                if (!CompanionIgnoresQuit) test._windows.Running.Remove(CompanionPid);
                OnQuit?.Invoke();
                return Json("""{"ok":true}""");
            }

            return new(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}
