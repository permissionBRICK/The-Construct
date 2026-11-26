using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Construct.Agent.Tests.Support;
using Construct.Agent.Update;

namespace Construct.Agent.Tests;

/// <summary>The agent updating itself against a fake channel: hash check, switch, self-test at start and the way back.</summary>
public sealed class SelfUpdateTests : IAsyncLifetime
{
    private const string Old = "1.0.0+1111111111111111111111111111111111111111";
    private const string New = "1.1.0+2222222222222222222222222222222222222222";
    private const string Channel = "https://updates.example/construct/manifest.json";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "agent-test-update-" + Guid.NewGuid().ToString("n"));
    private readonly byte[] _package = Package();
    private string _published;
    private FakeDevice? _device;
    private FakeDevice.StaticHandler? _channel;

    public SelfUpdateTests() => _published = Convert.ToHexStringLower(SHA256.HashData(_package));

    private FakeDevice Device(string? running = Old)
    {
        var handler = new FakeDevice.StaticHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            Channel => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new System.Text.Json.Nodes.JsonObject
                {
                    ["channel"] = "nightly",
                    ["version"] = New,
                    ["commit"] = "2222222222222222222222222222222222222222",
                    ["releaseTag"] = "host-2222222222222222222222222222222222222222",
                    ["packages"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["agentLinux"] = new System.Text.Json.Nodes.JsonObject { ["asset"] = "construct-agent-2222222-linux-x64.zip", ["sha256"] = _published },
                        ["agentWindows"] = new System.Text.Json.Nodes.JsonObject { ["asset"] = "construct-agent-2222222-win-x64.zip", ["sha256"] = new string('0', 64) },
                    },
                }.ToJsonString()),
            },
            "https://updates.example/construct/construct-agent-2222222-linux-x64.zip" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_package) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        _channel = handler;
        _device = new FakeDevice(_directory, channel: handler, running: running, version: Old);
        _device.Settings.Update(s => s with { Channel = Channel });
        if (running is not null)
        {
            // The running version as the install script left it.
            Directory.CreateDirectory(_device.Install.DirectoryOf(running));
            File.WriteAllText(_device.Install.ExecutableOf(running), "old");
            _device.Install.Write(new InstallState(running, null));
        }

        return _device;
    }

    [Fact]
    public async Task A_different_version_on_the_channel_is_on_offer_with_its_package_beside_a_url_channels_manifest()
    {
        var device = Device();

        var view = await device.Updater.CheckAsync(CancellationToken.None);

        Assert.Equal((Old, Channel, New, "idle", true), (view.Version, view.Channel, view.Available, view.State, view.Installed));
        Assert.Equal(new Uri("https://github.com/owner/repo/releases/download/host-abc/agent.zip"),
            AgentUpdater.Package(System.Text.Json.Nodes.JsonNode.Parse("""{"version":"1.0.0+x","releaseTag":"host-abc","packages":{"k":{"asset":"agent.zip","sha256":"%"}}}""".Replace("%", new string('a', 64))),
                new Uri("https://github.com/x"), "k", "owner/repo", github: true).Url);
    }

    [Fact]
    public async Task The_install_checks_the_hash_unpacks_beside_the_running_version_switches_and_stops()
    {
        var device = Device();
        await device.Updater.CheckAsync(CancellationToken.None);

        device.Updater.StartInstall();
        await device.Updater.Work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("agent v2", File.ReadAllText(device.Install.ExecutableOf(New)));
        Assert.Equal(new InstallState(New, Old, Pending: true), device.Install.Read());
        Assert.Equal([device.Install.ExecutableOf(New)], device.Platform.Launched);
        Assert.Equal(1, device.Stopped);
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(device.Install.VersionsDirectory), p => Path.GetFileName(p).StartsWith('.') || p.EndsWith(".partial", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_install_says_what_it_waits_for_and_goes_ahead_after_its_limit()
    {
        // Finding 21: "installs when the agent is idle" said nothing of what it waited for, and could wait forever.
        var device = Device();
        device.Busy = "a device pairing";
        await device.Updater.CheckAsync(CancellationToken.None);

        device.Updater.StartInstall();
        await FakeDevice.WaitAsync(() => device.Updater.View().Waiting == "a device pairing", "the reason");
        Assert.Equal("waiting", device.Updater.View().State);
        await device.Updater.Work.WaitAsync(TimeSpan.FromSeconds(10));

        // Still busy, but the limit (two seconds here, ten minutes by default) passed: the switch happened.
        Assert.Equal([device.Install.ExecutableOf(New)], device.Platform.Launched);
        Assert.Null(device.Updater.View().Waiting);
    }

    [Fact]
    public async Task The_new_version_opens_the_window_and_the_pop_out_that_were_open()
    {
        // Finding 5: the window did not come back after a self-update.
        var device = Device();
        device.Updater.OpenSurfaces = () => ["win", "pop", "alone"];
        await device.Updater.CheckAsync(CancellationToken.None);

        device.Updater.StartInstall();
        await device.Updater.Work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([["win", "pop"]], device.Platform.LaunchedSurfaces);
    }

    [Fact]
    public async Task Opening_the_pop_out_checks_the_channel_at_most_once_per_quarter_hour()
    {
        // Finding 16: the agent checked only a minute after start and then every six hours.
        var device = Device();
        int Reads() { lock (_channel!.Requests) return _channel.Requests.Count(r => r.AbsoluteUri == Channel); }

        device.Updater.CheckIfStale();
        await FakeDevice.WaitAsync(() => device.Updater.View().CheckedAt is not null, "the first check");
        Assert.Equal(New, device.Updater.View().Available);
        device.Updater.CheckIfStale();
        device.Updater.CheckIfStale();
        await Task.Delay(200);

        Assert.Equal(1, Reads());
        Assert.Equal(TimeSpan.FromHours(1), device.Updater.CheckEvery);
        Assert.Equal(TimeSpan.FromMinutes(5), device.Updater.AutoCheckEvery);
    }

    [Fact]
    public async Task Automatic_install_stages_while_popout_is_open_and_switches_once_surfaces_close()
    {
        var device = Device();
        device.Settings.Update(s => s with { AutoUpdate = true });
        string[] open = ["pop"];
        device.Updater.OpenSurfaces = () => open;

        device.Updater.CheckIfStale();
        await FakeDevice.WaitAsync(() => Directory.Exists(device.Install.DirectoryOf(New)) && device.Updater.View().State == "idle", "package staged while pop-out is open");
        await device.Updater.Work.WaitAsync(TimeSpan.FromSeconds(10));

        // The running agent was NOT interrupted while the pop-out was open.
        Assert.Empty(device.Platform.Launched);
        Assert.Equal(0, device.Stopped);
        int Downloads() { lock (_channel!.Requests) return _channel.Requests.Count(r => r.AbsoluteUri.EndsWith(".zip", StringComparison.Ordinal)); }
        Assert.Equal(1, Downloads());

        // While the pop-out stays open and the version is already staged, TryAutoInstall is a no-op.
        Assert.False(device.Updater.TryAutoInstall());

        // Once the user closes the pop-out, TryAutoInstall switches immediately using the staged directory with no extra download.
        open = [];
        Assert.True(device.Updater.TryAutoInstall());
        await device.Updater.Work.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([device.Install.ExecutableOf(New)], device.Platform.Launched);
        Assert.Equal([[]], device.Platform.LaunchedSurfaces);
        Assert.Equal(1, device.Stopped);
        Assert.Equal(1, Downloads());
    }

    [Fact]
    public async Task Check_if_stale_installs_automatically_when_auto_update_is_on()
    {
        var device = Device();
        device.Settings.Update(s => s with { AutoUpdate = true });

        device.Updater.CheckIfStale();
        await FakeDevice.WaitAsync(() => device.Platform.Launched.Count == 1, "automatic install after stale check");
        await device.Updater.Work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([device.Install.ExecutableOf(New)], device.Platform.Launched);
        Assert.Equal(1, device.Stopped);
    }

    [Fact]
    public async Task A_failed_automatic_install_is_not_retried_automatically_until_requested_manually()
    {
        var device = Device();
        device.Settings.Update(s => s with { AutoUpdate = true });
        var goodHash = _published;
        _published = new string('f', 64);

        device.Updater.CheckIfStale();
        await FakeDevice.WaitAsync(() => device.Updater.View().State == "failed", "failed auto-install");
        await device.Updater.Work.WaitAsync(TimeSpan.FromSeconds(10));
        int Downloads() { lock (_channel!.Requests) return _channel.Requests.Count(r => r.AbsoluteUri.EndsWith(".zip", StringComparison.Ordinal)); }
        Assert.Equal(1, Downloads());

        // Subsequent stale checks and TryAutoInstall do not hammer the same failed version.
        Assert.False(device.Updater.TryAutoInstall());
        device.Updater.CheckIfStale();
        await Task.Delay(150);
        Assert.Equal(1, Downloads());

        // An explicit manual install clears the failure guard and succeeds once fixed.
        _published = goodHash;
        await device.Updater.CheckAsync(CancellationToken.None);
        device.Updater.StartInstall();
        await device.Updater.Work.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([device.Install.ExecutableOf(New)], device.Platform.Launched);
    }

    private sealed class DelayedManifestHandler(string url, string sha256, TaskCompletionSource entered, TaskCompletionSource release) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri == url)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(new System.Text.Json.Nodes.JsonObject
                    {
                        ["channel"] = "nightly",
                        ["version"] = New,
                        ["commit"] = "2222222222222222222222222222222222222222",
                        ["releaseTag"] = "host-2222222222222222222222222222222222222222",
                        ["packages"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["agentLinux"] = new System.Text.Json.Nodes.JsonObject { ["asset"] = "construct-agent-2222222-linux-x64.zip", ["sha256"] = sha256 },
                        },
                    }.ToJsonString()),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Channel_change_during_an_in_flight_check_discards_the_old_channels_offer()
    {
        var manifestEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseManifest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new DelayedManifestHandler(Channel, _published, manifestEntered, releaseManifest);
        _device = new FakeDevice(_directory, channel: handler, running: Old, version: Old);
        _device.Settings.Update(s => s with { Channel = Channel, AutoUpdate = true });
        Directory.CreateDirectory(_device.Install.DirectoryOf(Old));
        File.WriteAllText(_device.Install.ExecutableOf(Old), "old");
        _device.Install.Write(new InstallState(Old, null));

        var checkTask = _device.Updater.CheckAsync(CancellationToken.None);
        await manifestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _device.Settings.Update(s => s with { Channel = "https://other.example/manifest.json" });
        _device.Updater.ChannelChanged();
        releaseManifest.SetResult();
        await checkTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(_device.Updater.View().Available);
        Assert.Equal("idle", _device.Updater.View().State);
        Assert.False(_device.Updater.TryAutoInstall());
        Assert.Empty(_device.Platform.Launched);
    }

    [Fact]
    public async Task Automatic_install_defers_while_agent_is_busy_instead_of_cutting_it_short()
    {
        var device = Device();
        device.Settings.Update(s => s with { AutoUpdate = true });
        device.Busy = "the update of hosts and VMs";
        await device.Updater.CheckAsync(CancellationToken.None);

        Assert.False(device.Updater.TryAutoInstall());
        Assert.Equal("idle", device.Updater.View().State);
        Assert.Empty(device.Platform.Launched);
    }

    [Fact]
    public async Task Download_timeout_records_failure_and_suppresses_automatic_retry()
    {
        var handler = new FakeDevice.StaticHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            Channel => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new System.Text.Json.Nodes.JsonObject
                {
                    ["channel"] = "nightly",
                    ["version"] = New,
                    ["commit"] = "2222222222222222222222222222222222222222",
                    ["releaseTag"] = "host-2222222222222222222222222222222222222222",
                    ["packages"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["agentLinux"] = new System.Text.Json.Nodes.JsonObject { ["asset"] = "construct-agent-2222222-linux-x64.zip", ["sha256"] = _published },
                    },
                }.ToJsonString()),
            },
            _ => throw new TaskCanceledException("Simulated HTTP timeout"),
        });
        _device = new FakeDevice(_directory, channel: handler, running: Old, version: Old);
        _device.Settings.Update(s => s with { Channel = Channel, AutoUpdate = true });
        Directory.CreateDirectory(_device.Install.DirectoryOf(Old));
        File.WriteAllText(_device.Install.ExecutableOf(Old), "old");
        _device.Install.Write(new InstallState(Old, null));

        await _device.Updater.CheckAsync(CancellationToken.None);
        Assert.True(_device.Updater.TryAutoInstall());
        await _device.Updater.Work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("failed", _device.Updater.View().State);
        Assert.False(_device.Updater.TryAutoInstall());
    }

    [Fact]
    public async Task A_package_whose_hash_differs_from_the_manifest_is_not_installed()
    {
        var device = Device();
        _published = new string('f', 64);
        await device.Updater.CheckAsync(CancellationToken.None);

        device.Updater.StartInstall();
        await device.Updater.Work.WaitAsync(TimeSpan.FromSeconds(10));

        var view = device.Updater.View();
        Assert.Equal("failed", view.State);
        Assert.Contains("SHA-256", view.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(device.Install.DirectoryOf(New)));
        Assert.Equal(new InstallState(Old, null), device.Install.Read());
        Assert.Empty(device.Platform.Launched);
        Assert.Equal(0, device.Stopped);
        Assert.Single(Directory.GetFileSystemEntries(device.Install.VersionsDirectory));
    }

    [Fact]
    public async Task A_new_version_that_fails_its_self_test_starts_the_previous_one_again()
    {
        var device = Device();
        await device.Updater.CheckAsync(CancellationToken.None);
        device.Updater.StartInstall();
        await device.Updater.Work.WaitAsync(TimeSpan.FromSeconds(10));

        var ok = await AgentUpdater.StartupAsync(device.Install, New, () => throw new InvalidOperationException("no health"), device.Platform);

        Assert.False(ok);
        Assert.Equal(new InstallState(Old, null), device.Install.Read());
        Assert.Equal([device.Install.ExecutableOf(New), device.Install.ExecutableOf(Old)], device.Platform.Launched);
        // The previous one then starts as a plain start.
        Assert.True(await AgentUpdater.StartupAsync(device.Install, Old, () => Task.CompletedTask, device.Platform));
    }

    [Fact]
    public async Task A_new_version_that_passes_confirms_itself_and_keeps_only_itself_and_the_previous_one()
    {
        var device = Device();
        Directory.CreateDirectory(device.Install.DirectoryOf("0.9.0+older"));
        await device.Updater.CheckAsync(CancellationToken.None);
        device.Updater.StartInstall();
        await device.Updater.Work.WaitAsync(TimeSpan.FromSeconds(10));

        var confirmed = new List<string>();
        Assert.True(await AgentUpdater.StartupAsync(device.Install, New, () => Task.CompletedTask, device.Platform, confirmed: confirmed.Add));

        // What follows the agent (the VS Code extension) is told once, with the new version's directory.
        Assert.Equal([device.Install.DirectoryOf(New)], confirmed);
        Assert.True(await AgentUpdater.StartupAsync(device.Install, New, () => Task.CompletedTask, device.Platform, confirmed: confirmed.Add));
        Assert.Single(confirmed);
        Assert.Equal(new InstallState(New, Old), device.Install.Read());
        Assert.Equal([AgentInstall.DirectoryName(New), AgentInstall.DirectoryName(Old)],
            Directory.GetDirectories(device.Install.VersionsDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal).Reverse());
    }

    [Fact]
    public async Task A_build_not_run_from_an_installed_package_does_not_update_itself()
    {
        var device = Device(running: null);
        await device.Updater.CheckAsync(CancellationToken.None);

        Assert.Equal("not-installed", Assert.Throws<AgentFailure>(() => device.Updater.StartInstall()).Code);
        Assert.False(device.Updater.View().Installed);
    }

    [Fact]
    public void Running_version_is_read_from_the_executables_place()
    {
        var install = new Update.AgentInstall(_directory, "construct-agent");
        install.Write(new InstallState(New, Old));
        Assert.Equal(New, install.RunningVersion(install.ExecutableOf(New)));
        Assert.Null(install.RunningVersion("/usr/local/bin/construct-agent"));
    }

    private static byte[] Package()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("construct-agent").Open(), new UTF8Encoding(false))) writer.Write("agent v2");
            using (var writer = new StreamWriter(zip.CreateEntry("ui/index.html").Open(), new UTF8Encoding(false))) writer.Write("<!doctype html>");
        }

        return stream.ToArray();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_device is not null) await _device.DisposeAsync();
        try { Directory.Delete(_directory, true); }
        catch (DirectoryNotFoundException) { }
    }
    [Fact]
    public async Task The_extension_of_a_version_is_installed_into_vs_code_when_both_are_there()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("construct-vsix-").FullName;
        try
        {
            var version = Path.Combine(root, "v"); Directory.CreateDirectory(version);
            var bin = Path.Combine(root, "bin"); Directory.CreateDirectory(bin);
            var log = Path.Combine(root, "args");

            // No package, then no VS Code: nothing to do, and never a failure of the start.
            Assert.Equal("no-package", await VsCodeExtension.InstallAsync(version, bin));
            File.WriteAllText(Path.Combine(version, VsCodeExtension.FileName), "x");
            Assert.Equal("no-vscode", await VsCodeExtension.InstallAsync(version, bin));

            var code = Path.Combine(bin, "code");
            File.WriteAllText(code, $"#!/bin/sh\nprintf '%s\\n' \"$@\" > '{log}'\nexit \"$(cat '{root}/exit' 2>/dev/null || echo 0)\"\n");
            File.SetUnixFileMode(code, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Equal("installed", await VsCodeExtension.InstallAsync(version, bin));
            Assert.Equal(["--install-extension", Path.Combine(version, VsCodeExtension.FileName), "--force"], File.ReadAllLines(log));

            File.WriteAllText(Path.Combine(root, "exit"), "3");
            Assert.Equal("failed", await VsCodeExtension.InstallAsync(version, bin));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
