using System.Text.Json;
using Constructd.Api.Admin;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Windows;
using Constructd.Windows.Iso;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Constructd.Tests.Api;

/// <summary>
/// <c>constructd admin iso …</c> — how the install media exists at all in the default
/// <see cref="IsoBuildMode.Prebuilt"/> mode: the installing administrator runs this, because the
/// service (LocalSystem) cannot run WSL.
///
/// Driven against the real <see cref="FileIsoCatalog"/> over a fake file system, so the versioned
/// name, the atomic pointer swap and "never overwrite an attached ISO" are exercised as shipped; only
/// the build itself is faked.
/// </summary>
public sealed class AdminIsoCliTests
{
    private const string CacheDir = PlatformOptions.CacheDir;

    [Fact]
    public async Task Build_publishes_a_versioned_iso_and_reports_its_path_on_the_last_line()
    {
        var cli = new Cli();

        var exit = await cli.RunAsync("iso", "build");

        Assert.Equal(AdminExitCode.Ok, exit);

        // The installer parses this line, which is why it is last and why it is exactly this shape.
        var expected = $@"{CacheDir}\construct-autoinstall-20260902T141530Z.iso";
        Assert.Equal($"ISO: {expected}", cli.Output.TrimEnd().Split(Environment.NewLine)[^1]);
        Assert.True(cli.Files.FileExists(expected));
        Assert.True(cli.Files.FileExists(expected + ".json"));
    }

    [Fact]
    public async Task Build_asks_for_generic_media_with_the_configured_identity_source()
    {
        var cli = new Cli();

        await cli.RunAsync("iso", "build");

        var request = Assert.Single(cli.Builder.Requests);
        Assert.Equal("hyperv-kvp", request.HostnameSource);
        Assert.Equal("construct", request.SeedUser);
        Assert.Equal($@"{CacheDir}\construct-autoinstall-20260902T141530Z.iso", request.OutputPath);

        // Generated per build and discarded: nobody logs in with it, and it is not printed, stored or
        // audited anywhere.
        Assert.NotEmpty(request.SeedPassword);
        Assert.DoesNotContain(request.SeedPassword, cli.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(request.SeedPassword, cli.Error, StringComparison.Ordinal);
        var audit = await cli.Audit.QueryAsync(50, CancellationToken.None);
        Assert.DoesNotContain(
            audit,
            entry => (entry.Detail ?? string.Empty).Contains(request.SeedPassword, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Build_records_what_went_into_the_media()
    {
        var cli = new Cli();

        await cli.RunAsync("iso", "build", "--json");

        var payload = JsonSerializer.Deserialize<JsonElement>(cli.Output);
        Assert.Equal("2026-09-02T14:15:30+00:00", payload.GetProperty("builtAt").GetString());
        Assert.Equal(@"C:\isos\ubuntu-24.04.3-live-server-amd64.iso", payload.GetProperty("sourceIso").GetString());
        Assert.Equal("abc123", payload.GetProperty("sourceSha256").GetString());
        Assert.Equal("SHA256:AAAA", payload.GetProperty("bootstrapKeyFingerprint").GetString());
        Assert.Equal("hyperv-kvp", payload.GetProperty("hostnameSource").GetString());
        Assert.Equal("script-hash", payload.GetProperty("scriptSha256").GetString());
        Assert.Equal("construct", payload.GetProperty("seedUser").GetString());
    }

    [Fact]
    public async Task Build_is_idempotent_so_re_running_the_installer_is_cheap()
    {
        var cli = new Cli();
        await cli.RunAsync("iso", "build");
        cli.Reset();

        var exit = await cli.RunAsync("iso", "build");

        Assert.Equal(AdminExitCode.Ok, exit);
        Assert.Single(cli.Builder.Requests);
        Assert.Contains("already built", cli.Output, StringComparison.Ordinal);
        Assert.Contains("--force", cli.Output, StringComparison.Ordinal);
        // Still the parseable last line, so the installer does not care which path it took.
        Assert.StartsWith("ISO: ", cli.Output.TrimEnd().Split(Environment.NewLine)[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Force_builds_a_new_file_and_never_overwrites_the_one_in_use()
    {
        // Hyper-V holds an open handle on an attached ISO: a rebuild that wrote over it would corrupt
        // the media a VM is installing from right now.
        var cli = new Cli();
        await cli.RunAsync("iso", "build");
        var first = $@"{CacheDir}\construct-autoinstall-20260902T141530Z.iso";

        cli.Clock.UtcNow = cli.Clock.UtcNow.AddHours(2);
        cli.Reset();

        await cli.RunAsync("iso", "build", "--force");
        var second = $@"{CacheDir}\construct-autoinstall-20260902T161530Z.iso";

        Assert.Equal(2, cli.Builder.Requests.Count);
        Assert.True(cli.Files.FileExists(first));
        Assert.True(cli.Files.FileExists(second));
        Assert.Equal(second, cli.Catalog.GetCurrent()!.Path);
        Assert.Contains($"ISO: {second}", cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_build_that_produces_nothing_leaves_the_previous_media_current()
    {
        var cli = new Cli();
        await cli.RunAsync("iso", "build");
        var first = cli.Catalog.GetCurrent()!.Path;

        cli.Clock.UtcNow = cli.Clock.UtcNow.AddHours(2);
        cli.Builder.BuiltSize = 0;
        cli.Reset();

        var exit = await cli.RunAsync("iso", "build", "--force");

        Assert.Equal(AdminExitCode.Failed, exit);
        Assert.Equal(first, cli.Catalog.GetCurrent()!.Path);
    }

    [Fact]
    public async Task A_failed_build_reports_safely_and_changes_nothing()
    {
        var cli = new Cli();
        cli.Builder.Failure = IsoBuildException.ForMedia("the WSL build exited with 2");

        var exit = await cli.RunAsync("iso", "build");

        Assert.Equal(AdminExitCode.Failed, exit);
        Assert.Contains("the WSL build exited with 2", cli.Error, StringComparison.Ordinal);
        Assert.Null(cli.Catalog.GetCurrent());
    }

    [Fact]
    public async Task Build_writes_an_audit_entry_naming_the_media_and_not_the_secret()
    {
        var cli = new Cli();

        await cli.RunAsync("iso", "build");

        var audit = await cli.Audit.QueryAsync(50, CancellationToken.None);
        var entry = Assert.Single(audit, item => item.Action == "iso.build");
        Assert.Equal("construct-autoinstall-20260902T141530Z.iso", entry.Target);
        Assert.Contains("hostnameSource=hyperv-kvp", entry.Detail);
    }

    [Fact]
    public async Task Status_says_what_is_published_and_from_what()
    {
        var cli = new Cli();
        await cli.RunAsync("iso", "build");
        cli.Reset();

        var exit = await cli.RunAsync("iso", "status");

        Assert.Equal(AdminExitCode.Ok, exit);
        Assert.Contains("Prebuilt", cli.Output, StringComparison.Ordinal);
        Assert.Contains("construct-autoinstall-20260902T141530Z.iso", cli.Output, StringComparison.Ordinal);
        Assert.Contains("SHA256:AAAA", cli.Output, StringComparison.Ordinal);
        Assert.Contains("hyperv-kvp", cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_reports_not_found_when_no_media_is_published()
    {
        // Exit code 3, so the installer and a health check can branch on "this host cannot create VMs".
        var cli = new Cli();

        var exit = await cli.RunAsync("iso", "status");

        Assert.Equal(AdminExitCode.NotFound, exit);
        Assert.Contains("constructd admin iso build", cli.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_refuses_media_it_cannot_describe()
    {
        var cli = new Cli();
        await cli.RunAsync("iso", "build");
        cli.Files.DeleteFile(cli.Catalog.GetCurrent()!.Path + ".json");
        cli.Reset();

        var exit = await cli.RunAsync("iso", "status");

        Assert.Equal(AdminExitCode.NotFound, exit);
        Assert.Contains("sidecar", cli.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prune_removes_superseded_media_and_keeps_the_current_one()
    {
        var cli = new Cli();
        await cli.RunAsync("iso", "build");
        cli.Clock.UtcNow = cli.Clock.UtcNow.AddHours(2);
        await cli.RunAsync("iso", "build", "--force");
        cli.Reset();

        var exit = await cli.RunAsync("iso", "prune", "--json");

        Assert.Equal(AdminExitCode.Ok, exit);
        var payload = JsonSerializer.Deserialize<JsonElement>(cli.Output);
        Assert.Equal(
            "construct-autoinstall-20260902T141530Z.iso",
            payload.GetProperty("removed")[0].GetString());
        Assert.True(cli.Files.FileExists($@"{CacheDir}\construct-autoinstall-20260902T161530Z.iso"));
    }

    [Fact]
    public async Task Prune_keeps_an_iso_a_vm_still_has_attached_and_says_why()
    {
        var cli = new Cli();
        await cli.RunAsync("iso", "build");
        var attached = cli.Catalog.GetCurrent()!.Path;
        cli.Files.Locked.Add(attached);

        cli.Clock.UtcNow = cli.Clock.UtcNow.AddHours(2);
        await cli.RunAsync("iso", "build", "--force");
        cli.Reset();

        var exit = await cli.RunAsync("iso", "prune");

        Assert.Equal(AdminExitCode.Ok, exit);
        Assert.True(cli.Files.FileExists(attached));
        Assert.Contains("in use", cli.Output, StringComparison.Ordinal);
        Assert.Contains("No ISO was removed", cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_iso_verbs_report_a_host_that_cannot_hold_media_rather_than_crashing()
    {
        // A Linux or fake-mode host: the stores work, the platform does not.
        var cli = new Cli(withPlatform: false);

        foreach (var verb in new[] { "build", "status", "prune" })
        {
            cli.Reset();
            Assert.Equal(AdminExitCode.Failed, await cli.RunAsync("iso", verb));
            Assert.Contains("Windows host", cli.Error, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("build", "--all")]
    [InlineData("status", "--force")]
    [InlineData("prune", "--force")]
    public async Task An_option_the_verb_does_not_take_is_a_usage_error(string verb, string option)
    {
        var cli = new Cli();

        Assert.Equal(AdminExitCode.Usage, await cli.RunAsync("iso", verb, option));
    }

    [Fact]
    public async Task The_usage_lists_the_iso_verbs()
    {
        var cli = new Cli();

        await cli.RunAsync("--help");

        Assert.Contains("iso build", cli.Output, StringComparison.Ordinal);
        Assert.Contains("iso status", cli.Output, StringComparison.Ordinal);
        Assert.Contains("iso prune", cli.Output, StringComparison.Ordinal);
    }

    /// <summary>The CLI over the real catalog, a fake build and in-memory stores.</summary>
    private sealed class Cli
    {
        private readonly ServiceProvider _services;
        private StringWriter _output = new();
        private StringWriter _error = new();

        public Cli(bool withPlatform = true)
        {
            Clock = new MutableClock { UtcNow = new DateTimeOffset(2026, 9, 2, 14, 15, 30, TimeSpan.Zero) };
            Files = new FakeIsoFileSystem();
            Builder = new FakeIsoMediaBuilder(Files);
            Catalog = new FileIsoCatalog(Files, Clock, CacheDir, NullLogger<FileIsoCatalog>.Instance);

            var services = new ServiceCollection();
            services.AddSingleton(PlatformOptions.Create());
            services.AddSingleton<IClock>(Clock);
            services.AddSingleton<InMemoryAuditLog>();
            services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<InMemoryAuditLog>());

            if (withPlatform)
            {
                services.AddSingleton<IIsoCatalog>(Catalog);
                services.AddSingleton<IIsoMediaBuilder>(Builder);
            }

            _services = services.BuildServiceProvider();
        }

        public MutableClock Clock { get; }

        public FakeIsoFileSystem Files { get; }

        public FakeIsoMediaBuilder Builder { get; }

        public FileIsoCatalog Catalog { get; }

        public IAuditLog Audit => _services.GetRequiredService<IAuditLog>();

        public string Output => _output.ToString();

        public string Error => _error.ToString();

        public void Reset()
        {
            _output = new StringWriter();
            _error = new StringWriter();
        }

        public Task<int> RunAsync(params string[] args) =>
            AdminCli.RunAsync(args, _services, _output, _error, CancellationToken.None);
    }
}
