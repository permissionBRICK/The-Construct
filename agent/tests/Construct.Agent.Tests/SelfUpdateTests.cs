using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Construct.Agent.Tests.Support;
using Construct.Agent.Update;

namespace Construct.Agent.Tests;

/// <summary>The agent updating itself against a fake channel: hash check, switch, self-test at start and the way back.</summary>
public sealed class SelfUpdateTests : IAsyncDisposable
{
    private const string Old = "1.0.0+1111111111111111111111111111111111111111";
    private const string New = "1.1.0+2222222222222222222222222222222222222222";
    private const string Channel = "https://updates.example/construct/manifest.json";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "agent-test-update-" + Guid.NewGuid().ToString("n"));
    private readonly byte[] _package = Package();
    private string _published;
    private FakeDevice? _device;

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

        Assert.True(await AgentUpdater.StartupAsync(device.Install, New, () => Task.CompletedTask, device.Platform));

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

    public async ValueTask DisposeAsync()
    {
        if (_device is not null) await _device.DisposeAsync();
        try { Directory.Delete(_directory, true); }
        catch (DirectoryNotFoundException) { }
    }
}
