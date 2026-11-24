using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Construct.Agent.Device;
using Construct.Agent.Events;
using Construct.Agent.Http;
using Construct.Agent.Tests.Support;
using Construct.Agent.Update;

namespace Construct.Agent.Tests;

/// <summary>
/// Patched T3 Code Desktop installation and updates on Windows: manifest validation, fresh install when a VM uses
/// patched T3 Code, background updates when closed, deferred-until-close updates when open, and explicit updates
/// (including inside "Update everything") that close and relaunch the app.
/// </summary>
public sealed class T3DesktopUpdaterTests : IAsyncLifetime
{
    private const string OldBuildHash = "44602226d51e7a3e13fcf8caf6d1a02f5e96bcae94995dbacc343f233c7e89dc";
    private const string OldPatchHash = "aa008e4326192895f29f1cca06e41edb0107dd35a7637e5a09adf1e38dadc34a";
    private const string NewBuildHash = "5783a76e777a30327ad461e52cdfb0268f5525b01e16e2c4499bab988ddbdc0f";
    private const string NewPatchHash = "b62ec5636323fd98e52421c3c01dab905a306523a818e0d5bd0f0709fc7a4442";
    private const string NightlyBuildHash = "b628a1d6ac7f818fc265c02f7d1801087c2c874a392e391261e6887f2239ed1e";
    private const string NightlyPatchHash = "c72ec5636323fd98e52421c3c01dab905a306523a818e0d5bd0f0709fc7a4443";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "agent-test-t3desktop-" + Guid.NewGuid().ToString("n"));
    private readonly List<IAsyncDisposable> _disposables = [];

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string StableManifestJson(string version, string buildHash, string patchHash, byte[] installerBytes, string repo = T3DesktopUpdater.DefaultRepository)
    {
        var tag = $"t3-{version}-{buildHash}";
        var url = $"https://github.com/{repo}/releases/download/{tag}/{T3DesktopUpdater.InstallerAssetName}";
        var sha = Sha256Hex(installerBytes);
        return $$"""
            {
              "formatVersion": 1,
              "channel": "stable",
              "version": "{{version}}",
              "buildHash": "{{buildHash}}",
              "patchHash": "{{patchHash}}",
              "releaseTag": "{{tag}}",
              "desktopVersion": "{{version}}-construct.{{buildHash[..8]}}",
              "sha256": "{{sha}}",
              "downloadUrl": "{{url}}",
              "assets": {
                "{{T3DesktopUpdater.InstallerAssetName}}": {
                  "url": "{{url}}",
                  "sha256": "{{sha}}",
                  "size": {{installerBytes.Length}}
                }
              }
            }
            """;
    }

    private static string NightlyManifestJson(string version, string buildHash, string patchHash, byte[] installerBytes, string repo = T3DesktopUpdater.DefaultRepository)
    {
        var tag = $"t3-{version}-{buildHash}";
        var url = $"https://github.com/{repo}/releases/download/{tag}/{T3DesktopUpdater.InstallerAssetName}";
        var sha = Sha256Hex(installerBytes);
        return $$"""
            {
              "formatVersion": 1,
              "channel": "nightly",
              "version": "{{version}}",
              "buildHash": "{{buildHash}}",
              "patchHash": "{{patchHash}}",
              "releaseTag": "{{tag}}",
              "desktopVersion": "{{version}}-construct.{{buildHash[..8]}}",
              "sha256": "{{sha}}",
              "downloadUrl": "{{url}}",
              "assets": {
                "{{T3DesktopUpdater.InstallerAssetName}}": {
                  "url": "{{url}}",
                  "sha256": "{{sha}}",
                  "size": {{installerBytes.Length}}
                }
              }
            }
            """;
    }

    private FakeEngines EnginesWithPatchedVm(string channel = "stable", bool patched = true) =>
        new FakeEngines().Add("eng1")
            .On("eng1", "GET whoami", """{"name":"alice","role":"user"}""")
            .On("eng1", "GET vms", """[{"name":"dev","owner":"alice","kind":"primary","deleting":false}]""")
            .On("eng1", "GET vms/dev/desired", $$$$"""{"desired":{"settings":{"t3Code":"true","t3CodePatched":"{{{{(patched ? "true" : "false")}}}}","t3CodeChannel":"{{{{channel}}}}"}}}""")
            .On("eng1", "POST me/vms/reprovision", """{"vms":["dev"]}""");

    private FakeT3DesktopPlatform MakePlatform(bool installed, bool running, string? existingRecordJson = null)
    {
        var artifacts = Path.Combine(_directory, "artifacts-" + Guid.NewGuid().ToString("n")[..6]);
        Directory.CreateDirectory(artifacts);
        if (existingRecordJson is not null) File.WriteAllText(Path.Combine(artifacts, "installed.json"), existingRecordJson, Encoding.UTF8);
        return new FakeT3DesktopPlatform(artifacts) { Installed = installed, Running = running };
    }

    private T3DesktopUpdater MakeUpdater(IT3DesktopPlatform? platform, FakeEngines engines, FakeDevice.StaticHandler http, bool autoUpdate = true, AgentEvents? events = null)
    {
        Directory.CreateDirectory(_directory);
        var settings = new DeviceSettings(Path.Combine(_directory, "device-" + Guid.NewGuid().ToString("n")[..6] + ".json"));
        settings.Update(s => s with { AutoUpdate = autoUpdate });
        var updater = new T3DesktopUpdater(platform, engines, settings, new HttpClient(http), () => { }, TimeProvider.System, events)
        {
            FirstCheck = TimeSpan.FromDays(1),
            CheckEvery = TimeSpan.FromDays(1),
            ClosePoll = TimeSpan.FromMilliseconds(20),
        };
        _disposables.Add(updater);
        return updater;
    }

    [Fact]
    public void NeedsInstall_matches_the_patch_hash_rule_and_requires_the_app_on_disk()
    {
        var bytes = "fake-installer"u8.ToArray();
        var release = T3DesktopUpdater.ParseManifest(JsonNode.Parse(StableManifestJson("0.1.11", NewBuildHash, NewPatchHash, bytes)), "stable");

        Assert.True(T3DesktopUpdater.NeedsInstall(null, release, appPresent: true));
        Assert.False(T3DesktopUpdater.NeedsInstall(new("0.1.11", "stable", OldBuildHash, NewPatchHash, "2026-11-24T00:00:00Z", "dev"), release, appPresent: true));
        Assert.True(T3DesktopUpdater.NeedsInstall(new("0.1.11", "stable", NewBuildHash, NewPatchHash, "2026-11-24T00:00:00Z", "dev"), release, appPresent: false));
        Assert.True(T3DesktopUpdater.NeedsInstall(new("0.1.11", "stable", OldBuildHash, OldPatchHash, "2026-11-24T00:00:00Z", "dev"), release, appPresent: true));
        Assert.True(T3DesktopUpdater.NeedsInstall(new("0.1.11", "nightly", NewBuildHash, NewPatchHash, "2026-11-24T00:00:00Z", "dev"), release, appPresent: true));
        // Empty patchHash falls back to buildHash.
        Assert.False(T3DesktopUpdater.NeedsInstall(new("0.1.11", "stable", NewBuildHash, "", "2026-11-24T00:00:00Z", "dev"), release, appPresent: true));
    }

    [Fact]
    public void ParseManifest_rejects_mismatched_urls_or_bad_hashes()
    {
        var bytes = "payload"u8.ToArray();
        var manifest = JsonNode.Parse(StableManifestJson("0.1.11", NewBuildHash, NewPatchHash, bytes))!.AsObject();
        manifest["assets"]![T3DesktopUpdater.InstallerAssetName]!["url"] = "https://evil.example/T3Code-Construct-Setup.exe";
        var ex = Assert.Throws<AgentFailure>(() => T3DesktopUpdater.ParseManifest(manifest, "stable"));
        Assert.Equal("t3-manifest-invalid", ex.Code);
    }

    [Fact]
    public async Task Background_auto_update_installs_silently_when_T3_Desktop_is_closed_and_does_not_launch_it()
    {
        var installer = "new-installer-bytes"u8.ToArray();
        var manifest = StableManifestJson("0.1.11", NewBuildHash, NewPatchHash, installer);
        var http = new FakeDevice.StaticHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/manifest.json", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) });
        var platform = MakePlatform(installed: true, running: false, existingRecordJson: $$"""
            {"t3Version":"0.1.11","channel":"stable","buildHash":"{{OldBuildHash}}","patchHash":"{{OldPatchHash}}","installedAt":"2026-11-21T00:00:00Z","sourceInstance":"dev"}
            """);
        var updater = MakeUpdater(platform, new FakeEngines(), http, autoUpdate: true);

        await updater.CheckAndAutoApplyAsync(allowFreshInstallWithoutAutoUpdate: false, CancellationToken.None);

        Assert.Single(platform.InstallerRuns);
        Assert.False(platform.InstallerRuns[0].AllowCloseRunning);
        Assert.Equal(0, platform.Launches);
        var record = updater.ReadInstalledRecord();
        Assert.NotNull(record);
        Assert.Equal(NewBuildHash, record!.BuildHash);
        Assert.Equal(NewPatchHash, record.PatchHash);
        Assert.Null(updater.View().Available);
        Assert.False(updater.View().WaitingForClose);
    }

    [Fact]
    public async Task Background_auto_update_waits_while_T3_Desktop_is_open_and_installs_once_it_closes()
    {
        var installer = "staged-installer-bytes"u8.ToArray();
        var manifest = StableManifestJson("0.1.11", NewBuildHash, NewPatchHash, installer);
        var downloads = 0;
        var http = new FakeDevice.StaticHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/manifest.json", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) };
            downloads++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) };
        });
        var platform = MakePlatform(installed: true, running: true, existingRecordJson: $$"""
            {"t3Version":"0.1.11","channel":"stable","buildHash":"{{OldBuildHash}}","patchHash":"{{OldPatchHash}}","installedAt":"2026-11-21T00:00:00Z","sourceInstance":"dev"}
            """);
        var updater = MakeUpdater(platform, new FakeEngines(), http, autoUpdate: true);

        await updater.CheckAndAutoApplyAsync(allowFreshInstallWithoutAutoUpdate: false, CancellationToken.None);

        // Downloaded and verified, but MUST NOT kill the open window.
        Assert.Equal(1, downloads);
        Assert.Empty(platform.InstallerRuns);
        Assert.True(updater.View().WaitingForClose);
        Assert.Equal("waiting", updater.View().State);

        // Now the user closes T3 Desktop: the close-watcher installs silently without relaunching.
        platform.Running = false;
        await FakeDevice.WaitAsync(() => platform.InstallerRuns.Count == 1 && !updater.View().WaitingForClose, "close-watcher install");

        Assert.False(platform.InstallerRuns[0].AllowCloseRunning);
        Assert.Equal(0, platform.Launches);
        Assert.Equal(1, downloads);
        Assert.Equal(NewBuildHash, updater.ReadInstalledRecord()?.BuildHash);
    }

    [Fact]
    public async Task Explicit_install_while_open_reuses_staged_installer_closes_and_relaunches_T3_Desktop()
    {
        var installer = "explicit-installer-bytes"u8.ToArray();
        var manifest = StableManifestJson("0.1.11", NewBuildHash, NewPatchHash, installer);
        var downloads = 0;
        var http = new FakeDevice.StaticHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/manifest.json", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) };
            downloads++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) };
        });
        var platform = MakePlatform(installed: true, running: true, existingRecordJson: $$"""
            {"t3Version":"0.1.11","channel":"stable","buildHash":"{{OldBuildHash}}","patchHash":"{{OldPatchHash}}","installedAt":"2026-11-21T00:00:00Z","sourceInstance":"dev"}
            """);
        var updater = MakeUpdater(platform, EnginesWithPatchedVm(), http, autoUpdate: true);

        // Stage in background while open.
        await updater.CheckAndAutoApplyAsync(allowFreshInstallWithoutAutoUpdate: false, CancellationToken.None);
        Assert.True(updater.View().WaitingForClose);
        Assert.Equal(1, downloads);

        // User clicks Update: reuses the staged file, runs --updated /S, relaunches T3 Desktop.
        updater.StartInstall();
        await updater.ExplicitWork.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, downloads);
        Assert.Single(platform.InstallerRuns);
        Assert.True(platform.InstallerRuns[0].AllowCloseRunning);
        Assert.Equal(1, platform.Launches);
        Assert.False(updater.View().WaitingForClose);
        Assert.Equal("idle", updater.View().State);
        Assert.Equal(NewBuildHash, updater.ReadInstalledRecord()?.BuildHash);
    }

    [Fact]
    public async Task Fresh_install_runs_when_a_VM_finishes_provisioning_with_t3CodePatched_true_even_with_autoUpdate_off()
    {
        var installer = "fresh-installer-bytes"u8.ToArray();
        var manifest = StableManifestJson("0.1.11", NewBuildHash, NewPatchHash, installer);
        var http = new FakeDevice.StaticHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/manifest.json", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) });
        var engines = EnginesWithPatchedVm(channel: "stable", patched: true)
            .On("eng1", "GET jobs/job-42", """{"id":"job-42","kind":"provision","state":"succeeded"}""");
        var platform = MakePlatform(installed: false, running: false);
        var events = new AgentEvents();
        var updater = MakeUpdater(platform, engines, http, autoUpdate: false, events: events);
        updater.Start();

        events.Publish("job.ended", "eng1", new JsonObject { ["id"] = "job-42", ["vm"] = "dev" });
        await FakeDevice.WaitAsync(() => platform.InstallerRuns.Count == 1 && updater.ReadInstalledRecord() is not null, "fresh post-provision install");

        Assert.False(platform.InstallerRuns[0].AllowCloseRunning);
        Assert.Equal(0, platform.Launches);
        Assert.True(platform.Installed);
        Assert.Equal("dev", updater.ReadInstalledRecord()?.SourceInstance);
        Assert.Equal(NewBuildHash, updater.ReadInstalledRecord()?.BuildHash);
    }

    [Fact]
    public async Task Does_not_install_when_no_VM_uses_patched_T3_Code_and_none_is_installed()
    {
        var http = new FakeDevice.StaticHandler();
        var platform = MakePlatform(installed: false, running: false);
        var updater = MakeUpdater(platform, EnginesWithPatchedVm(patched: false), http, autoUpdate: true);

        await updater.CheckAndAutoApplyAsync(allowFreshInstallWithoutAutoUpdate: true, CancellationToken.None);

        Assert.Empty(platform.InstallerRuns);
        Assert.Empty(http.Requests);
        Assert.Equal("not-needed", updater.View().State);
    }

    [Fact]
    public async Task Nightly_channel_discovers_newest_complete_prerelease()
    {
        var installer = "nightly-installer"u8.ToArray();
        var version = "0.1.12-nightly.20261124.5178";
        var tag = $"t3-{version}-{NightlyBuildHash}";
        var manifest = NightlyManifestJson(version, NightlyBuildHash, NightlyPatchHash, installer);
        var listing = $$"""
            [
              {"draft":false,"prerelease":true,"tag_name":"t3-0.1.12-nightly.20261124.9999-{{OldBuildHash}}","published_at":"2026-11-24T12:00:00Z","assets":[{"name":"manifest.json"}]},
              {"draft":false,"prerelease":true,"tag_name":"{{tag}}","published_at":"2026-11-24T10:00:00Z","assets":[{"name":"manifest.json"},{"name":"SHA256SUMS"},{"name":"{{T3DesktopUpdater.InstallerAssetName}}"},{"name":"{{T3DesktopUpdater.ServerAssetName}}"}]}
            ]
            """;
        var http = new FakeDevice.StaticHandler(req => req.RequestUri!.AbsoluteUri.Contains("/releases?", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listing) }
            : req.RequestUri.AbsolutePath.EndsWith("/manifest.json", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) });
        var platform = MakePlatform(installed: false, running: false);
        var updater = MakeUpdater(platform, EnginesWithPatchedVm(channel: "nightly", patched: true), http, autoUpdate: true);

        var view = await updater.CheckAsync(CancellationToken.None);

        Assert.Equal("nightly", view.Channel);
        Assert.Equal(version, view.Available);
    }

    [Fact]
    public async Task Hash_mismatch_fails_without_running_installer_or_writing_record()
    {
        var expected = "expected-bytes"u8.ToArray();
        var tampered = "tampered-bytes"u8.ToArray();
        var manifest = StableManifestJson("0.1.11", NewBuildHash, NewPatchHash, expected);
        var http = new FakeDevice.StaticHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/manifest.json", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tampered) });
        var platform = MakePlatform(installed: true, running: false, existingRecordJson: $$"""
            {"t3Version":"0.1.11","channel":"stable","buildHash":"{{OldBuildHash}}","patchHash":"{{OldPatchHash}}","installedAt":"2026-11-21T00:00:00Z","sourceInstance":"dev"}
            """);
        var updater = MakeUpdater(platform, new FakeEngines(), http, autoUpdate: true);

        var outcome = await updater.RunForUpdateEverythingAsync(null, CancellationToken.None);

        Assert.Equal("failed", outcome.State);
        Assert.Contains("SHA-256", outcome.Detail, StringComparison.Ordinal);
        Assert.Empty(platform.InstallerRuns);
        Assert.Equal(OldBuildHash, updater.ReadInstalledRecord()?.BuildHash);
    }

    [Fact]
    public async Task UpdateEverything_updates_T3_Desktop_before_device_and_relaunches_it_when_running()
    {
        var installer = "everything-installer"u8.ToArray();
        var manifest = StableManifestJson("0.1.11", NewBuildHash, NewPatchHash, installer);
        var http = new FakeDevice.StaticHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/manifest.json", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) });
        var platform = MakePlatform(installed: true, running: true, existingRecordJson: $$"""
            {"t3Version":"0.1.11","channel":"stable","buildHash":"{{OldBuildHash}}","patchHash":"{{OldPatchHash}}","installedAt":"2026-11-21T00:00:00Z","sourceInstance":"dev"}
            """);
        var engines = EnginesWithPatchedVm();
        var settings = new DeviceSettings(Path.Combine(_directory, "everything-device.json"));
        var options = new AgentOptions { DataDirectory = _directory };
        var install = new AgentInstall(_directory, "construct-agent");
        var fakePlatform = new FakePlatform();
        _disposables.Add(new DisposableAdapter(fakePlatform));
        var t3Desktop = new T3DesktopUpdater(platform, engines, settings, new HttpClient(http), () => { }, TimeProvider.System);
        _disposables.Add(t3Desktop);
        var self = new AgentUpdater(options, install, settings, new HttpClient(new FakeDevice.StaticHandler()), "agentLinux", () => null, fakePlatform, () => { }, () => { });
        _disposables.Add(self);
        var everything = new UpdateEverything(engines, self, () => { }, TimeProvider.System, t3Desktop) { PollInterval = TimeSpan.FromMilliseconds(10) };

        everything.Start();
        await everything.Work.WaitAsync(TimeSpan.FromSeconds(10));

        var parts = everything.View().Parts.Select(p => (p.Part, p.State)).ToArray();
        Assert.Equal([("engine", "skipped"), ("vms", "done"), ("t3-desktop", "done"), ("device", "skipped")], parts);
        Assert.Single(platform.InstallerRuns);
        Assert.True(platform.InstallerRuns[0].AllowCloseRunning);
        Assert.Equal(1, platform.Launches);
        Assert.Equal(NewBuildHash, t3Desktop.ReadInstalledRecord()?.BuildHash);
    }

    [Fact]
    public async Task Health_advertises_t3_desktop_update_and_routes_answer_expected_codes()
    {
        Directory.CreateDirectory(_directory);
        var hostDir = Path.Combine(_directory, "host-routes");
        await using var host = AgentHost.Create(new AgentOptions { DataDirectory = hostDir, Protection = "file", PublishEndpoint = false });
        await host.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var health = JsonNode.Parse(await client.GetStringAsync("/api/health"))!;
        var features = health["apiFeatures"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Contains("t3-desktop-update", features);

        using var getReq = new HttpRequestMessage(HttpMethod.Get, "/api/t3-desktop/update");
        getReq.Headers.Authorization = new("Bearer", host.Token);
        using var getRes = await client.SendAsync(getReq);
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);
        var view = JsonNode.Parse(await getRes.Content.ReadAsStringAsync())!;
        Assert.False(view["supported"]!.GetValue<bool>());
        Assert.Equal("unsupported", view["state"]!.GetValue<string>());

        using var installReq = new HttpRequestMessage(HttpMethod.Post, "/api/t3-desktop/update/install");
        installReq.Headers.Authorization = new("Bearer", host.Token);
        using var installRes = await client.SendAsync(installReq);
        Assert.Equal(HttpStatusCode.NotImplemented, installRes.StatusCode);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var item in _disposables) await item.DisposeAsync();
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        catch (IOException) { }
    }

    private sealed class FakeT3DesktopPlatform(string artifactDirectory) : IT3DesktopPlatform
    {
        public string ArtifactDirectory { get; } = artifactDirectory;
        public bool Installed { get; set; }
        public bool Running { get; set; }
        public int ExitCode { get; set; }
        public int Launches { get; private set; }
        public List<(string Path, bool AllowCloseRunning)> InstallerRuns { get; } = [];

        public bool IsInstalled() => Installed;
        public bool IsRunning() => Running;

        public Task<int> RunInstallerAsync(string installerPath, bool allowCloseRunning, CancellationToken ct)
        {
            InstallerRuns.Add((installerPath, allowCloseRunning));
            if (ExitCode == 0)
            {
                Installed = true;
                if (allowCloseRunning) Running = false;
            }

            return Task.FromResult(ExitCode);
        }

        public Task<bool> LaunchAsync(CancellationToken ct)
        {
            Launches++;
            Running = true;
            return Task.FromResult(true);
        }
    }

    private sealed class DisposableAdapter(IDisposable inner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
