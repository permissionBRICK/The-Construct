using Constructd.Api.Composition;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Constructd.Windows.Iso;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Constructd.Tests.Windows;

/// <summary>
/// The ISO catalog and the strategy that consumes it (plan §4.10).
///
/// Two rules here come from Hyper-V rather than from taste, and both are what these tests are for:
/// media is never overwritten in place (an attached ISO is held open, and a VM installing right now
/// is reading it), and the pointer swap is atomic. The third is a service rule: media whose sidecar
/// cannot be read is refused, because "which bootstrap key is in this ISO?" has no answer then.
/// </summary>
public sealed class IsoCatalogTests
{
    private const string CacheDir = PlatformOptions.CacheDir;

    /// <summary>A real ed25519 public key line — the fingerprint is computed from its blob.</summary>
    private const string RotatedKey =
        "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFJSUlJSUlJSUlJSUlJSUlJSUlJSUlJSUlJSUlJSUlJS bootstrap@construct";

    private static readonly IsoSidecar Sidecar = new(
        new DateTimeOffset(2026, 9, 2, 14, 15, 30, TimeSpan.Zero),
        @"C:\isos\ubuntu-24.04.3-live-server-amd64.iso",
        "abc123",
        "construct",
        "SHA256:AAAA",
        "hyperv-kvp",
        "script-hash");

    [Fact]
    public void The_media_path_is_versioned_by_utc_build_time()
    {
        var (catalog, _, clock) = Catalog();
        clock.UtcNow = new DateTimeOffset(2026, 9, 2, 14, 15, 30, TimeSpan.Zero);

        Assert.Equal($@"{CacheDir}\construct-autoinstall-20260902T141530Z.iso", catalog.NextMediaPath());
    }

    [Fact]
    public void Two_builds_in_the_same_second_get_different_names()
    {
        // Two administrators, two processes: nothing in this service can serialize them, so the name
        // has to be reserved rather than merely computed. Handing both the same path would have both
        // xorriso runs write the same file -- the one thing this catalog must never allow.
        var (catalog, files, _) = Catalog();

        var first = catalog.NextMediaPath();
        var second = catalog.NextMediaPath();

        Assert.NotEqual(first, second);
        Assert.Equal($@"{CacheDir}\construct-autoinstall-20260902T141530Z.iso", first);
        Assert.Equal($@"{CacheDir}\construct-autoinstall-20260902T141530Z-2.iso", second);

        // Both names are taken the moment they are handed out.
        Assert.True(files.FileExists(first));
        Assert.True(files.FileExists(second));
    }

    [Fact]
    public void A_name_that_already_exists_is_never_handed_out_again()
    {
        // A clock that went backwards (or a restored cache directory) must not produce a path that
        // points at media somebody may still be installing from.
        var (catalog, files, _) = Catalog();
        var taken = $@"{CacheDir}\construct-autoinstall-20260902T141530Z.iso";
        files.WithBinary(taken, 3_000_000_000);
        files.Locked.Add(taken);   // and Hyper-V is holding it open

        var path = catalog.NextMediaPath();

        Assert.NotEqual(taken, path);
        // Untouched: the existing media still has its bytes.
        Assert.Equal(3_000_000_000, files.FileLength(taken));
        Assert.StartsWith($@"{CacheDir}\construct-autoinstall-20260902T141530Z-", path, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reservation_left_by_a_failed_build_is_pruned()
    {
        var (catalog, files, clock) = Catalog();
        var published = catalog.NextMediaPath();
        files.WithBinary(published, 10);
        catalog.Publish(published, Sidecar);

        clock.UtcNow = clock.UtcNow.AddHours(1);
        var abandoned = catalog.NextMediaPath();   // reserved, then the build failed

        var result = catalog.Prune();

        Assert.Contains(NameOf(abandoned), result.Removed);
        Assert.False(files.FileExists(abandoned));
        Assert.True(files.FileExists(published));
    }

    [Fact]
    public void An_empty_catalog_has_nothing_current()
    {
        var (catalog, _, _) = Catalog();

        Assert.Null(catalog.GetCurrent());
        Assert.Empty(catalog.List());
    }

    [Fact]
    public void Publishing_writes_the_sidecar_and_swaps_the_pointer_atomically()
    {
        var (catalog, files, _) = Catalog();
        var iso = $@"{CacheDir}\construct-autoinstall-20260902T141530Z.iso";
        files.WithBinary(iso, 3_000_000_000);

        var entry = catalog.Publish(iso, Sidecar);

        Assert.True(entry.IsCurrent);
        Assert.Equal("construct-autoinstall-20260902T141530Z.iso", entry.FileName);

        // The sidecar sits next to the ISO and is real JSON we can read back.
        Assert.True(files.FileExists(iso + ".json"));

        // The swap: written beside the pointer, then renamed over it. A reader sees the old name or
        // the new one, never half a file — and a VM may be installing from the old ISO right now.
        Assert.Contains(
            files.Moves,
            move => move.Source == $@"{CacheDir}\current.pointer.tmp" && move.Destination == $@"{CacheDir}\current.pointer");
        Assert.Equal("construct-autoinstall-20260902T141530Z.iso", files.Files[$@"{CacheDir}\current.pointer"].Trim());
    }

    [Fact]
    public void A_published_entry_reads_back_with_everything_the_sidecar_recorded()
    {
        var (catalog, files, _) = Catalog();
        var iso = $@"{CacheDir}\construct-autoinstall-20260902T141530Z.iso";
        files.WithBinary(iso, 42);
        catalog.Publish(iso, Sidecar);

        var current = catalog.GetCurrent();

        Assert.NotNull(current);
        Assert.Equal(Sidecar, current!.Sidecar);
        Assert.Equal(42, current.SizeBytes);
        Assert.Equal(iso, current.Path);
    }

    [Fact]
    public void A_second_build_never_overwrites_the_first_one_and_becomes_current()
    {
        // Hyper-V holds an open handle on an attached ISO, so a rebuild must land on a new file.
        var (catalog, files, clock) = Catalog();

        var first = catalog.NextMediaPath();
        files.WithBinary(first, 10);
        catalog.Publish(first, Sidecar);

        clock.UtcNow = clock.UtcNow.AddHours(1);
        var second = catalog.NextMediaPath();
        Assert.NotEqual(first, second);

        files.WithBinary(second, 20);
        catalog.Publish(second, Sidecar with { BuiltAt = clock.UtcNow });

        Assert.Equal(second, catalog.GetCurrent()!.Path);
        // Both are still on disk: nothing was replaced.
        Assert.True(files.FileExists(first));
        Assert.Equal(2, catalog.List().Count);
        Assert.Single(catalog.List(), entry => entry.IsCurrent);
    }

    [Fact]
    public void Publishing_a_missing_or_empty_iso_is_refused_and_leaves_the_pointer_alone()
    {
        var (catalog, files, _) = Catalog();
        var good = $@"{CacheDir}\construct-autoinstall-20260902T141530Z.iso";
        files.WithBinary(good, 10);
        catalog.Publish(good, Sidecar);

        var truncated = $@"{CacheDir}\construct-autoinstall-20260902T151530Z.iso";
        files.WithBinary(truncated, 0);

        Assert.Throws<InvalidOperationException>(() => catalog.Publish(truncated, Sidecar));
        Assert.Equal(good, catalog.GetCurrent()!.Path);
    }

    [Fact]
    public void A_pointer_naming_a_file_that_is_gone_is_not_current_media()
    {
        var (catalog, files, _) = Catalog();
        var iso = $@"{CacheDir}\construct-autoinstall-20260902T141530Z.iso";
        files.WithBinary(iso, 10);
        catalog.Publish(iso, Sidecar);

        files.DeleteFile(iso);

        Assert.Null(catalog.GetCurrent());
    }

    [Theory]
    [InlineData(@"..\..\Windows\System32\evil.iso")]
    [InlineData(@"C:\Windows\Temp\evil.iso")]
    [InlineData("evil.iso")]
    [InlineData("construct-autoinstall-../../evil.iso")]
    public void A_pointer_that_does_not_name_a_catalog_file_is_ignored(string content)
    {
        // The pointer decides which file the service hands to Hyper-V. Following a path out of the
        // cache directory would make a writable pointer a way to boot a VM from anything at all.
        var (catalog, files, _) = Catalog();
        files.WithFile($@"{CacheDir}\current.pointer", content);

        Assert.Null(catalog.GetCurrent());
    }

    [Fact]
    public void Prune_removes_superseded_media_with_its_sidecar_and_keeps_the_current_one()
    {
        var (catalog, files, clock) = Catalog();

        var old = catalog.NextMediaPath();
        files.WithBinary(old, 10);
        catalog.Publish(old, Sidecar);

        clock.UtcNow = clock.UtcNow.AddDays(1);
        var current = catalog.NextMediaPath();
        files.WithBinary(current, 20);
        catalog.Publish(current, Sidecar);

        var result = catalog.Prune();

        Assert.Equal(["construct-autoinstall-20260902T141530Z.iso"], result.Removed);
        Assert.False(files.FileExists(old));
        Assert.False(files.FileExists(old + ".json"));
        Assert.True(files.FileExists(current));
        Assert.Contains(result.Skipped, skip => skip.FileName == catalog.GetCurrent()!.FileName);
    }

    [Fact]
    public void Prune_skips_an_iso_a_vm_still_has_attached_and_says_so()
    {
        var (catalog, files, clock) = Catalog();

        var attached = catalog.NextMediaPath();
        files.WithBinary(attached, 10);
        catalog.Publish(attached, Sidecar);
        files.Locked.Add(attached);   // Hyper-V holds the handle

        clock.UtcNow = clock.UtcNow.AddDays(1);
        var current = catalog.NextMediaPath();
        files.WithBinary(current, 20);
        catalog.Publish(current, Sidecar);

        var result = catalog.Prune();

        Assert.Empty(result.Removed);
        Assert.True(files.FileExists(attached));
        Assert.Contains(result.Skipped, skip => skip.Reason.Contains("in use", StringComparison.Ordinal));
    }

    // ── The consuming side ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_prebuilt_builder_hands_back_the_current_media_and_says_what_it_is()
    {
        var (catalog, files, _) = Catalog();
        var iso = $@"{CacheDir}\construct-autoinstall-20260902T141530Z.iso";
        files.WithBinary(iso, 10);
        catalog.Publish(iso, Sidecar);

        var builder = Prebuilt(catalog, files);
        var progress = new ProgressSink();

        var path = await builder.BuildAsync(
            "work-vm", "construct", "seed-secret", @"C:\Construct\keys\bootstrap_ed25519.pub", progress,
            CancellationToken.None);

        Assert.Equal(iso, path);
        Assert.Contains(progress.Lines, line => line.Contains("construct-autoinstall-20260902T141530Z.iso", StringComparison.Ordinal));
        Assert.Contains(progress.Lines, line => line.Contains("SHA256:AAAA", StringComparison.Ordinal));
        Assert.Contains(progress.Lines, line => line.Contains("hyperv-kvp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_prebuilt_builder_ignores_the_vm_name_and_the_seed_password()
    {
        // Generic media: one ISO serves every VM, and nothing per-VM is baked into it. The seed
        // password has nowhere to go either — there is no build here to hand it to.
        var (catalog, files, _) = Catalog();
        var iso = $@"{CacheDir}\construct-autoinstall-20260902T141530Z.iso";
        files.WithBinary(iso, 10);
        catalog.Publish(iso, Sidecar);

        var builder = Prebuilt(catalog, files);
        var progress = new ProgressSink();

        var first = await builder.BuildAsync("work-vm", "construct", "seed-secret", string.Empty, progress, CancellationToken.None);
        var second = await builder.BuildAsync("other-vm", "construct", "another-secret", string.Empty, progress, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.DoesNotContain(progress.Lines, line => line.Contains("seed-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(progress.Lines, line => line.Contains("work-vm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_media_at_all_fails_with_the_command_that_fixes_it()
    {
        var (catalog, files, _) = Catalog();
        var builder = Prebuilt(catalog, files);

        var ex = await Assert.ThrowsAsync<IsoNotBuiltException>(() => builder.BuildAsync(
            "work-vm", "construct", "seed-secret", string.Empty, null, CancellationToken.None));

        Assert.Contains("constructd admin iso build", ex.Message, StringComparison.Ordinal);

        // It reaches the job error, the audit trail and the API response as it is — so it must say
        // something useful and carry nothing else.
        Assert.Equal(ex.Message, SafeError.Describe(ex));
        Assert.DoesNotContain("seed-secret", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Media_whose_sidecar_cannot_be_read_is_refused()
    {
        // The sidecar is the only record of what an ISO contains. An ISO nobody can describe is not
        // one to install a host's VMs from, and rebuilding it is one command.
        var (catalog, files, _) = Catalog();
        var iso = $@"{CacheDir}\construct-autoinstall-20260902T141530Z.iso";
        files.WithBinary(iso, 10);
        catalog.Publish(iso, Sidecar);
        files.DeleteFile(iso + ".json");

        var builder = Prebuilt(catalog, files);

        var ex = await Assert.ThrowsAsync<IsoNotBuiltException>(() => builder.BuildAsync(
            "work-vm", "construct", "seed-secret", string.Empty, null, CancellationToken.None));

        Assert.Contains("sidecar", ex.Message, StringComparison.Ordinal);
        Assert.Contains("constructd admin iso build", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sidecar_that_is_not_json_is_treated_the_same_way()
    {
        var (catalog, files, _) = Catalog();
        var iso = $@"{CacheDir}\construct-autoinstall-20260902T141530Z.iso";
        files.WithBinary(iso, 10);
        catalog.Publish(iso, Sidecar);
        files.WithFile(iso + ".json", "{ this is not json");

        var builder = Prebuilt(catalog, files);

        await Assert.ThrowsAsync<IsoNotBuiltException>(() => builder.BuildAsync(
            "work-vm", "construct", "seed-secret", string.Empty, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_rotated_bootstrap_key_is_reported_before_the_vm_is_created()
    {
        // The failure that looks like success: the VM installs, boots, and then refuses the client's
        // key. Loud in the job log, because the remedy is a rebuild.
        var (catalog, files, _) = Catalog();
        var iso = $@"{CacheDir}\construct-autoinstall-20260902T141530Z.iso";
        files.WithBinary(iso, 10);
        catalog.Publish(iso, Sidecar with { BootstrapKeyFingerprint = "SHA256:STALE" });

        var key = @"C:\Construct\keys\bootstrap_ed25519.pub";
        files.WithFile(key, RotatedKey);

        var progress = new ProgressSink();
        await Prebuilt(catalog, files).BuildAsync(
            "work-vm", "construct", "seed-secret", key, progress, CancellationToken.None);

        Assert.Contains(progress.Lines, line =>
            line.Contains("WARNING", StringComparison.Ordinal) &&
            line.Contains("SHA256:STALE", StringComparison.Ordinal) &&
            line.Contains("--force", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_matching_bootstrap_key_produces_no_warning()
    {
        var (catalog, files, _) = Catalog();
        var iso = $@"{CacheDir}\construct-autoinstall-20260902T141530Z.iso";
        files.WithBinary(iso, 10);

        var key = @"C:\Construct\keys\bootstrap_ed25519.pub";
        var keyText = RotatedKey;
        files.WithFile(key, keyText);
        catalog.Publish(iso, Sidecar with { BootstrapKeyFingerprint = SshPublicKey.FingerprintOrUnknown(keyText) });

        var progress = new ProgressSink();
        await Prebuilt(catalog, files).BuildAsync(
            "work-vm", "construct", "seed-secret", key, progress, CancellationToken.None);

        Assert.DoesNotContain(progress.Lines, line => line.Contains("WARNING", StringComparison.Ordinal));
    }

    // ── Mode wiring ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(IsoBuildMode.Prebuilt, typeof(PrebuiltIsoBuilder))]
    [InlineData(IsoBuildMode.PerVm, typeof(WslIsoBuilder))]
    public void The_mode_decides_which_strategy_the_job_gets(IsoBuildMode mode, Type expected)
    {
        using var provider = Strategy(mode);

        Assert.IsType(expected, provider.GetRequiredService<IIsoBuilder>());
    }

    [Fact]
    public void The_media_builder_is_registered_whatever_the_mode_is()
    {
        // `admin iso build` must work in Prebuilt mode — that is the whole point of the mode — and it
        // is the interactive administrator's WSL that does the building either way.
        foreach (var mode in new[] { IsoBuildMode.Prebuilt, IsoBuildMode.PerVm })
        {
            using var provider = Strategy(mode);

            Assert.IsType<WslIsoBuilder>(provider.GetRequiredService<IIsoMediaBuilder>());
            Assert.IsType<FileIsoCatalog>(provider.GetRequiredService<IIsoCatalog>());
        }
    }

    [Theory]
    [InlineData(IsoBuildMode.Native)]
    [InlineData(IsoBuildMode.InGuest)]
    [InlineData(IsoBuildMode.HypervisorHost)]
    public void A_planned_strategy_is_refused_with_what_to_use_instead(IsoBuildMode mode)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Strategy(mode));

        Assert.Contains("not implemented", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Prebuilt", ex.Message, StringComparison.Ordinal);
        Assert.Contains("PerVm", ex.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider Strategy(IsoBuildMode mode)
    {
        var options = PlatformOptions.Create(o => o.Iso.Mode = mode);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(options);
        services.AddSingleton<IClock>(new MutableClock());
        services.AddSingleton<IIsoFileSystem>(new FakeIsoFileSystem());
        services.AddSingleton<IProcessRunner>(new RecordingProcessRunner());
        services.AddSingleton<IIsoDownloader>(new FakeIsoDownloader(new FakeIsoFileSystem()));
        services.AddIsoStrategy(options);

        return services.BuildServiceProvider();
    }

    private static PrebuiltIsoBuilder Prebuilt(IIsoCatalog catalog, IIsoFileSystem files) =>
        new(catalog, files, PlatformOptions.Create(), NullLogger<PrebuiltIsoBuilder>.Instance);

    private static string NameOf(string path) => path[(path.LastIndexOf('\\') + 1)..];

    private static (FileIsoCatalog Catalog, FakeIsoFileSystem Files, MutableClock Clock) Catalog()
    {
        var files = new FakeIsoFileSystem();
        var clock = new MutableClock { UtcNow = new DateTimeOffset(2026, 9, 2, 14, 15, 30, TimeSpan.Zero) };

        return (new FileIsoCatalog(files, clock, CacheDir, NullLogger<FileIsoCatalog>.Instance), files, clock);
    }
}
