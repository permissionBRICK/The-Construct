using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Constructd.Api.Contracts;
using Constructd.Api.Hosting;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Constructd.Windows.Updates;
using Microsoft.Extensions.DependencyInjection;
namespace Constructd.Tests.Updates;

/// <summary>
/// The one update action (12.1, <c>POST /host/updates/run</c>) against the fake release source: the real
/// stager downloads and checks the package named by the channel manifest, the fake launcher stands in for the
/// updater, whose outcome (files swapped, or put back) is fed to the recovery service as the updater writes it.
/// </summary>
public sealed class RunUpdateTests : IDisposable
{
    private const string ChannelUrl = "https://github.com/permissionBRICK/The-Construct/releases/latest/download/manifest.json";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "run-update-" + Guid.NewGuid().ToString("n"));
    private readonly PackageTests _packages = new();
    public RunUpdateTests() => Directory.CreateDirectory(_root);
    public void Dispose() { _packages.Dispose(); Directory.Delete(_root, true); }

    private TestApp App() => new(new Dictionary<string, string?> { ["Constructd:DatabasePath"] = Path.Combine(_root, "db"), ["Constructd:ScriptsDir"] = _root },
        s => s.AddSingleton(PackageTests.WindowsStager).AddSingleton<IUpdateStager>(sp => sp.GetRequiredService<PackageStager>()));

    /// <summary>Publishes a channel manifest offering a release of commit aaaa…; answers it and the package bytes.</summary>
    private static async Task<(ReleaseManifest Manifest, byte[] Zip)> PublishAsync(TestApp app, PackageTests packages, bool auto = false, bool badPackage = false)
    {
        var (m, zip) = packages.Package();
        m = m with { PayloadSizeBytes = zip.Length };
        await app.Service<IHostConfigStore>().SetAsync("updates", HostAdminDefaults.Updates with { Auto = auto }, "test", default);
        var json = JsonSerializer.SerializeToNode(m, UpdateFiles.Json)!.AsObject();
        json["channel"] = "stable";
        json["version"] = "1.2.3+" + m.Commit;
        json["guestPayloadHash"] = new string('c', 64);
        json["packages"] = new JsonObject { ["payload"] = new JsonObject { ["asset"] = m.PayloadAsset, ["sha256"] = m.PayloadSha256 } };
        var source = app.Service<FakeReleaseSource>();
        source.ChannelManifests[new Uri(ChannelUrl)] = JsonSerializer.SerializeToUtf8Bytes(json);
        var served = badPackage ? zip.Select((b, i) => i == zip.Length / 2 ? (byte)(b ^ 1) : b).ToArray() : zip;
        source.Assets[new Uri($"https://github.com/permissionBRICK/The-Construct/releases/download/{m.ReleaseTag}/{m.PayloadAsset}")] = served;
        return (m, zip);
    }

    private static async Task<JsonElement> StatusAsync(HttpClient admin) => await admin.GetFromJsonAsync<JsonElement>("/api/v1/host/updates/status");

    private static async Task<HostUpdateRunResponse> RunAsync(HttpClient admin, HttpStatusCode expected)
    {
        var response = await admin.PostAsync("/api/v1/host/updates/run", null);
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HostUpdateRunResponse>())!;
    }

    [Fact]
    public async Task Up_to_date_host_starts_nothing()
    {
        using var app = App();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var (m, _) = await PublishAsync(app, _packages);
        ((FakeReleaseInfo)app.Service<IReleaseInfo>()).Installed = new(m.Commit, "1.2.3", null, "release");
        var answer = await RunAsync(admin, HttpStatusCode.OK);
        Assert.Equal("up-to-date", answer.State);
        Assert.Null(answer.JobId);
        Assert.Empty(await app.Service<IHostUpdateStore>().ListAsync(10, default));
        Assert.Equal(0, app.Service<FakeUpdaterLauncher>().LaunchCount);
        var status = await StatusAsync(admin);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("available").ValueKind);
        Assert.Equal("main", status.GetProperty("channel").GetString());
        Assert.False(status.GetProperty("auto").GetBoolean());
    }

    [Theory]
    [InlineData("succeeded", HostUpdateState.Succeeded)]
    [InlineData("rolledBack", HostUpdateState.RolledBack)]
    public async Task Newer_release_is_downloaded_checked_handed_off_and_settles(string outcome, HostUpdateState settled)
    {
        using var app = App();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var (m, _) = await PublishAsync(app, _packages);
        var status = await StatusAsync(admin);
        Assert.Equal("1.2.3+" + m.Commit, status.GetProperty("available").GetProperty("version").GetString());
        Assert.False(string.IsNullOrEmpty(status.GetProperty("version").GetString()));

        var answer = await RunAsync(admin, HttpStatusCode.Accepted);
        Assert.Equal("started", answer.State);
        var job = await admin.WaitForJobAsync(answer.JobId!);
        Assert.True(job.State == JobState.Succeeded, job.Error);
        var launcher = app.Service<FakeUpdaterLauncher>();
        Assert.Equal(1, launcher.LaunchCount);
        var h = (await launcher.ReadHandoffAsync(default))!;
        Assert.Equal(m.Commit, h.Commit);
        Assert.False(h.RestoreDatabase); // the updater keeps the database copy and never restores it
        var row = (await app.Service<IHostUpdateStore>().GetAsync(answer.UpdateId!, default))!;
        Assert.Equal(HostUpdateState.HandedOff, row.State);
        Assert.Contains(row.Phases, p => p.Name == "download");
        Assert.Contains(row.Phases, p => p.Name == "drain");
        Assert.Equal(MaintenanceState.Maintenance, app.Service<IMaintenanceGate>().State);

        // What the updater leaves behind: the database copy in the backup, the previous ledger, and its outcome.
        var backup = Path.Combine(_root, "updates", "backup-" + h.UpdateId);
        Directory.CreateDirectory(backup);
        await File.WriteAllTextAsync(Path.Combine(backup, "constructd.db"), "copy");
        if (outcome == "rolledBack")
        {
            // The previous files are back in place: the recovery service checks them against the backup's ledger.
            await File.WriteAllTextAsync(Path.Combine(_root, "old.txt"), "old");
            await UpdateFiles.WriteAsync(Path.Combine(backup, "previous-install.json"), new InstallRecord(h.PreviousCommit, "old", DateTimeOffset.UtcNow, null, null,
                [new UpdateFile("scripts/old.txt", Convert.ToHexStringLower(SHA256.HashData("old"u8.ToArray())))]), default);
        }
        launcher.Recovery = new(h.UpdateId, h.Commit, h.PreviousCommit, outcome == "succeeded" ? "commit" : "rollback", DateTimeOffset.UtcNow, outcome,
            outcome == "succeeded" ? null : "update-health-failed", backup, true, true, h.StagedPath, 1, []);
        await app.Service<UpdateRecoveryService>().ReconcileAsync(default);
        Assert.Equal(MaintenanceState.Open, app.Service<IMaintenanceGate>().State);
        Assert.Equal(settled, (await app.Service<IHostUpdateStore>().GetAsync(answer.UpdateId!, default))!.State);

        status = await StatusAsync(admin);
        Assert.Equal(answer.UpdateId, status.GetProperty("current").GetProperty("updateId").GetString());
        Assert.Equal(outcome, status.GetProperty("current").GetProperty("state").GetString());
        Assert.Equal(Path.Combine(backup, "constructd.db"), status.GetProperty("databaseCopy").GetString());
        Assert.Equal("copy", await File.ReadAllTextAsync(Path.Combine(backup, "constructd.db")));
    }

    [Fact]
    public async Task Package_whose_hash_does_not_match_the_channel_manifest_is_never_handed_off()
    {
        using var app = App();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await PublishAsync(app, _packages, badPackage: true);
        var answer = await RunAsync(admin, HttpStatusCode.Accepted);
        var job = await admin.WaitForJobAsync(answer.JobId!);
        Assert.Equal(JobState.Failed, job.State);
        var row = (await app.Service<IHostUpdateStore>().GetAsync(answer.UpdateId!, default))!;
        Assert.Equal(HostUpdateState.StageFailed, row.State);
        Assert.Equal("payload-hash-mismatch", row.Error);
        Assert.Equal(0, app.Service<FakeUpdaterLauncher>().LaunchCount);
        Assert.Null(await app.Service<FakeUpdaterLauncher>().ReadHandoffAsync(default));
        Assert.Equal(MaintenanceState.Open, app.Service<IMaintenanceGate>().State);
    }

    [Fact]
    public async Task Channel_manifest_whose_package_hash_differs_from_its_release_keys_is_refused()
    {
        using var app = App();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var (m, _) = await PublishAsync(app, _packages);
        var source = app.Service<FakeReleaseSource>();
        var json = JsonNode.Parse(source.ChannelManifests[new Uri(ChannelUrl)])!;
        json["packages"]!["payload"]!["sha256"] = new string('0', 64);
        source.ChannelManifests[new Uri(ChannelUrl)] = JsonSerializer.SerializeToUtf8Bytes(json);
        var response = await admin.PostAsync("/api/v1/host/updates/run", null);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("release-source-invalid-metadata", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Empty(await app.Service<IHostUpdateStore>().ListAsync(10, default));
        Assert.Empty(source.Downloads.Where(d => d.Name == m.PayloadAsset));
    }

    [Fact]
    public async Task Only_an_admin_runs_the_host_update()
    {
        using var app = App();
        using var user = await app.CreateUserClientAsync("alice");
        await PublishAsync(app, _packages);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsync("/api/v1/host/updates/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/host/updates/status")).StatusCode);
    }

    [Fact]
    public async Task Old_apply_route_keeps_the_automatic_database_rollback()
    {
        using var app = App();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var (m, zip) = await PublishAsync(app, _packages);
        var source = app.Service<FakeReleaseSource>();
        var assets = new[] { ("manifest.json", JsonSerializer.SerializeToUtf8Bytes(m, UpdateFiles.Json)), (m.PayloadAsset, zip) }.Select(a =>
        {
            var uri = new Uri("https://github.com/permissionBRICK/The-Construct/releases/download/" + m.ReleaseTag + "/" + a.Item1);
            source.Assets[uri] = a.Item2; return new ReleaseAsset(a.Item1, uri, a.Item2.Length);
        }).ToArray();
        source.Releases.Add(new(m.ReleaseTag, m.Commit, DateTimeOffset.UtcNow, assets));
        var stage = await admin.PostAsJsonAsync("/api/v1/host/updates/stage", new { });
        var staged = await stage.Content.ReadFromJsonAsync<JsonElement>();
        await admin.WaitForJobAsync(staged.GetProperty("jobId").GetString()!);
        var apply = await admin.PostAsJsonAsync("/api/v1/host/updates/apply", new { updateId = staged.GetProperty("updateId").GetString() });
        Assert.Equal(HttpStatusCode.Accepted, apply.StatusCode);
        await admin.WaitForJobAsync((await apply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!);
        Assert.True((await app.Service<FakeUpdaterLauncher>().ReadHandoffAsync(default))!.RestoreDatabase);
    }

    [Fact]
    public async Task Automatic_update_runs_only_when_switched_on_and_not_again_after_a_failure()
    {
        using var app = App();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var (m, _) = await PublishAsync(app, _packages, auto: false);
        var auto = app.Service<HostAutoUpdateService>();
        Assert.Null(await auto.TickAsync(default, checkNow: true));
        Assert.Empty(await app.Service<IHostUpdateStore>().ListAsync(10, default));

        await PublishAsync(app, _packages, auto: true, badPackage: true);
        var started = Assert.IsType<HostUpdateStarted>(await auto.TickAsync(default, checkNow: true));
        await admin.WaitForJobAsync(started.JobId);
        Assert.Equal("auto-update", (await app.Service<IHostUpdateStore>().GetAsync(started.UpdateId, default))!.Actor);
        Assert.Equal(HostUpdateState.StageFailed, (await app.Service<IHostUpdateStore>().GetAsync(started.UpdateId, default))!.State);

        // The release that failed here is not tried again by itself; a later check without the auto setting does nothing either.
        Assert.Null(await auto.TickAsync(default, checkNow: true));
        Assert.Single(await app.Service<IHostUpdateStore>().ListAsync(10, default));
        Assert.Equal(m.Commit, (await app.Service<IHostUpdateStore>().GetAsync(started.UpdateId, default))!.Commit);
        var status = await StatusAsync(admin);
        Assert.True(status.GetProperty("auto").GetBoolean());
    }
}
