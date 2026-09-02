using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Windows.Internal;
using Constructd.Windows.Iso;
using Microsoft.Extensions.Logging.Abstractions;

namespace Constructd.Tests.Windows;

/// <summary>
/// The ISO build is the one step whose inputs decide what the guest becomes, and it reuses the repo's
/// own <c>bin/build-autoinstall-iso.sh</c> unchanged. These tests pin the exact <c>wsl.exe</c>
/// argument vector — the env contract, the LF-normalized script copy, the <c>/mnt/…</c> path mapping —
/// against what <c>Auto-Install.ps1</c> has always issued, so the guest payload cannot silently differ
/// between a local install and this service.
/// </summary>
public sealed class WslIsoBuilderTests
{
    private const string BuildScript = @"C:\Construct\bin\build-autoinstall-iso.sh";
    private const string LfScript = @"C:\Construct\bin\.build-autoinstall.lf.sh";
    private const string PubKey = @"C:\Construct\keys\bootstrap_ed25519.pub";
    private const string SourceIso = @"C:\isos\ubuntu-24.04.3-live-server-amd64.iso";
    private const string OutputIso = @"C:\ProgramData\Construct\service\iso\work-vm-autoinstall.iso";

    [Fact]
    public async Task The_wsl_command_line_is_the_one_auto_install_issues()
    {
        var (builder, runner, _, _) = Builder();

        var path = await BuildAsync(builder);

        var call = runner[0];
        Assert.Equal("wsl.exe", call.FileName);
        Assert.Equal(
            [
                "-d", "Ubuntu",
                "-u", "root",
                "--",
                "env",
                "VM_USER=construct",
                "VM_PASS=seed-secret",
                "VM_HOST=work-vm",
                "SOURCE_ID=ubuntu-server-minimal",
                "BOOTSTRAP_PUBKEY_FILE=/mnt/c/Construct/keys/bootstrap_ed25519.pub",
                "bash",
                "/mnt/c/Construct/bin/.build-autoinstall.lf.sh",
                "/mnt/c/isos/ubuntu-24.04.3-live-server-amd64.iso",
                "/mnt/c/ProgramData/Construct/service/iso/work-vm-autoinstall.iso",
            ],
            call.Arguments);

        // The path handed back is in the host's namespace, because that is what Hyper-V mounts.
        Assert.Equal(OutputIso, path);
    }

    [Fact]
    public async Task No_distro_selector_is_passed_when_none_is_configured()
    {
        var (builder, runner, _, _) = Builder(o => o.WslDistro = string.Empty);

        await BuildAsync(builder);

        Assert.DoesNotContain("-d", runner[0].Arguments);
        Assert.Equal("-u", runner[0].Arguments[0]);
    }

    [Fact]
    public async Task The_guest_hostname_is_the_vm_name_lowercased()
    {
        // The hostname is baked into the seed, which is why the ISO is per VM and rebuilt each time.
        var (builder, runner, _, _) = Builder();

        await BuildAsync(builder, vmName: "work-vm");

        Assert.Contains("VM_HOST=work-vm", runner[0].Arguments);
    }

    [Fact]
    public async Task The_build_script_is_copied_with_lf_line_endings_and_removed_afterwards()
    {
        var (builder, _, files, _) = Builder();
        files.Files[BuildScript] = "#!/usr/bin/env bash\r\nset -euo pipefail\r\ntrap 'x' EXIT\r\n";

        await BuildAsync(builder);

        // A CRLF copy breaks constructs like `trap` once bash reads it — the reason Auto-Install.ps1
        // normalizes it in the first place.
        Assert.Equal("#!/usr/bin/env bash\nset -euo pipefail\ntrap 'x' EXIT\n", files.Written[LfScript]);
        Assert.Contains(LfScript, files.Deleted);
    }

    [Fact]
    public async Task A_configured_source_iso_is_used_as_is_and_never_downloaded()
    {
        var (builder, runner, _, downloader) = Builder();

        await BuildAsync(builder);

        Assert.Empty(downloader.Downloads);
        Assert.Contains("/mnt/c/isos/ubuntu-24.04.3-live-server-amd64.iso", runner[0].Arguments);
    }

    [Fact]
    public async Task The_source_iso_is_downloaded_once_and_reused_by_later_builds()
    {
        var (builder, runner, files, downloader) = Builder(o =>
        {
            o.Iso.SourcePath = string.Empty;
            o.Iso.SourceUrl = "https://releases.ubuntu.com/24.04/ubuntu-24.04.3-live-server-amd64.iso";
        });
        files.WithBinary(@"C:\ProgramData\Construct\service\iso\other-vm-autoinstall.iso");

        await BuildAsync(builder);
        await BuildAsync(builder, vmName: "other-vm");

        var cached = @"C:\ProgramData\Construct\service\iso\ubuntu-24.04.3-live-server-amd64.iso";
        Assert.Single(downloader.Downloads);
        Assert.Equal(cached, downloader.Downloads[0].Destination);
        Assert.Contains(
            "/mnt/c/ProgramData/Construct/service/iso/ubuntu-24.04.3-live-server-amd64.iso",
            runner[1].Arguments);
    }

    [Fact]
    public async Task A_truncated_download_fails_instead_of_being_remastered()
    {
        var (builder, _, _, downloader) = Builder(o =>
        {
            o.Iso.SourcePath = string.Empty;
            o.Iso.SourceUrl = "https://releases.ubuntu.com/24.04/ubuntu.iso";
        });
        downloader.DownloadedSize = 0;

        await Assert.ThrowsAsync<IsoBuildException>(() => BuildAsync(builder));
    }

    [Fact]
    public async Task A_checksum_mismatch_fails_the_build()
    {
        var (builder, runner, files, _) = Builder(o => o.Iso.Sha256 = "abc123");
        files.Hashes[SourceIso] = "deadbeef";

        var ex = await Assert.ThrowsAsync<IsoBuildException>(() => BuildAsync(builder));

        Assert.Empty(runner.Calls);
        Assert.Contains("Sha256", ex.Detail);
    }

    [Fact]
    public async Task A_matching_checksum_is_accepted()
    {
        var (builder, runner, files, _) = Builder(o => o.Iso.Sha256 = "ABC123");
        files.Hashes[SourceIso] = "abc123";

        await BuildAsync(builder);

        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Neither_a_source_path_nor_a_url_is_a_configuration_error()
    {
        var (builder, _, _, _) = Builder(o =>
        {
            o.Iso.SourcePath = string.Empty;
            o.Iso.SourceUrl = string.Empty;
        });

        var ex = await Assert.ThrowsAsync<IsoBuildException>(() => BuildAsync(builder));

        Assert.Contains("Constructd:Iso:SourcePath", ex.Detail);
    }

    [Fact]
    public async Task A_failed_build_reports_a_message_that_cannot_carry_the_seed_password()
    {
        using var logs = new LogSink();
        var (builder, runner, _, _) = Builder(logs: logs);
        runner.Respond(new ProcessResult(
            2,
            string.Empty,
            "env VM_PASS=seed-secret bash: xorriso: command not found",
            TimedOut: false));

        var ex = await Assert.ThrowsAsync<IsoBuildException>(() => BuildAsync(builder));

        Assert.Equal("Building the autoinstall ISO for VM 'work-vm' failed.", ex.Message);
        Assert.Equal(ex.Message, SafeError.Describe(ex));

        // The exit code, not the build's output: bash and xorriso echo their arguments, and those
        // arguments carry VM_PASS.
        Assert.Equal("the WSL build exited with 2", ex.Detail);
        Assert.DoesNotContain("seed-secret", logs.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("xorriso", logs.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_build_output_ever_reaches_a_log_entry()
    {
        using var logs = new LogSink();
        var (builder, runner, files, _) = Builder(logs: logs);
        runner
            .Respond(new ProcessResult(2, "SENTINEL-OUT", "SENTINEL-ERR", TimedOut: false))
            .Respond(new ProcessResult(-1, "SENTINEL-OUT", "SENTINEL-ERR", TimedOut: true))
            .Respond(new ProcessResult(0, "SENTINEL-OUT", "SENTINEL-ERR", TimedOut: false));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<IsoBuildException>(() => BuildAsync(builder));
        }

        // …and the success path, whose output is streamed as (redacted) progress but never logged.
        await BuildAsync(builder);

        Assert.DoesNotContain("SENTINEL-OUT", logs.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL-ERR", logs.Text, StringComparison.Ordinal);
        Assert.False(files.Files.ContainsKey(LfScript));
    }

    [Fact]
    public async Task A_build_that_produces_no_iso_fails_even_when_wsl_reports_success()
    {
        var (builder, _, files, _) = Builder();
        files.Sizes.Remove(OutputIso);

        var ex = await Assert.ThrowsAsync<IsoBuildException>(() => BuildAsync(builder));

        Assert.Contains("missing or empty", ex.Detail);
    }

    [Fact]
    public async Task Progress_never_repeats_the_seed_password()
    {
        var (builder, runner, _, _) = Builder();
        runner.RespondStdout(
            "==> Identity   : user=construct host=work-vm (password preset)",
            "==> leaked: seed-secret");
        var progress = new ProgressSink();

        await builder.BuildAsync("work-vm", "construct", "seed-secret", PubKey, progress, CancellationToken.None);

        Assert.DoesNotContain(progress.Lines, line => line.Contains("seed-secret", StringComparison.Ordinal));
        Assert.Contains(progress.Lines, line => line.Contains("***", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Builds_are_serialized()
    {
        // One shared LF copy and one multi-gigabyte repack at a time: overlapping builds would write
        // each other's script file out from under them.
        var (builder, runner, files, _) = Builder();
        files.WithBinary(@"C:\ProgramData\Construct\service\iso\other-vm-autoinstall.iso");

        var running = 0;
        var maxConcurrent = 0;
        runner.Default = new ProcessResult(0, string.Empty, string.Empty, TimedOut: false);
        runner.Respond(call => Track(call));
        runner.Respond(call => Track(call));

        await Task.WhenAll(BuildAsync(builder), BuildAsync(builder, vmName: "other-vm"));

        Assert.Equal(1, maxConcurrent);

        ProcessResult Track(RecordedProcess call)
        {
            maxConcurrent = Math.Max(maxConcurrent, Interlocked.Increment(ref running));
            Thread.Sleep(20);
            Interlocked.Decrement(ref running);
            return new ProcessResult(0, string.Empty, string.Empty, TimedOut: false);
        }
    }

    [Fact]
    public async Task A_vm_name_that_is_not_a_dns_label_never_reaches_wsl()
    {
        var (builder, runner, _, _) = Builder();

        await Assert.ThrowsAsync<InvalidPlatformArgumentException>(
            () => BuildAsync(builder, vmName: "work-vm; rm -rf /"));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task A_newline_in_the_seed_password_is_refused_rather_than_split_into_another_variable()
    {
        var (builder, runner, _, _) = Builder();

        await Assert.ThrowsAsync<InvalidPlatformArgumentException>(
            () => builder.BuildAsync(
                "work-vm", "construct", "pass\nVM_HOST=evil", PubKey, null, CancellationToken.None));

        Assert.Empty(runner.Calls);
    }

    private static Task<string> BuildAsync(WslIsoBuilder builder, string vmName = "work-vm") =>
        builder.BuildAsync(vmName, "construct", "seed-secret", PubKey, null, CancellationToken.None);

    private static (WslIsoBuilder Builder, RecordingProcessRunner Runner, FakeIsoFileSystem Files, FakeIsoDownloader Downloader)
        Builder(Action<ConstructdOptions>? configure = null, LogSink? logs = null)
    {
        var options = PlatformOptions.Create(configure);
        var files = new FakeIsoFileSystem()
            .WithFile(BuildScript, "#!/usr/bin/env bash\r\nset -euo pipefail\r\n")
            .WithFile(PubKey, "ssh-ed25519 AAAA bootstrap@construct")
            .WithBinary(SourceIso, 3_000_000_000)
            .WithBinary(OutputIso, 2_000_000_000);

        var downloader = new FakeIsoDownloader(files);
        var runner = new RecordingProcessRunner();

        return (
            new WslIsoBuilder(
                runner,
                files,
                downloader,
                options,
                logs is null ? NullLogger<WslIsoBuilder>.Instance : logs.Logger<WslIsoBuilder>()),
            runner,
            files,
            downloader);
    }
}
