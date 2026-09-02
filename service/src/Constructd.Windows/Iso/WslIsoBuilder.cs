using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Logic;
using Constructd.Windows.Internal;
using Microsoft.Extensions.Logging;

namespace Constructd.Windows.Iso;

/// <summary>
/// The autoinstall ISO could not be built. As with the driver, the message names the VM and nothing
/// else, because it is what reaches the job error, the audit trail and the API response.
/// </summary>
public sealed class IsoBuildException : Exception, IConstructdError
{
    public IsoBuildException(string vmName, string? detail = null)
        : this($"Building the autoinstall ISO for VM '{vmName}' failed.", vmName, detail)
    {
    }

    private IsoBuildException(string message, string? vmName, string? detail)
        : base(message)
    {
        VmName = vmName;
        Detail = detail;
    }

    /// <summary>
    /// The same failure for a build that is not for one VM: generic media serves all of them, so
    /// naming a VM would be a lie.
    ///
    /// This one carries its reason in the message, because it is raised by <c>admin iso build</c> —
    /// an administrator standing at the host, not an API caller — and the reason is composed by this
    /// service (an exit code, a timeout, a configuration problem), never from the build's output.
    /// </summary>
    public static IsoBuildException ForMedia(string? detail = null) =>
        new(
            detail is null
                ? "Building the autoinstall ISO failed."
                : $"Building the autoinstall ISO failed: {detail}.",
            vmName: null,
            detail);

    /// <summary>The VM the build was for, or null for generic media.</summary>
    public string? VmName { get; }

    /// <summary>
    /// How it failed, in this service's own words (an exit code, a timeout, a configuration problem).
    /// Never the build's output.
    /// </summary>
    public string? Detail { get; }
}

/// <summary>
/// <see cref="IIsoBuilder"/> via WSL, running the repo's own <c>bin/build-autoinstall-iso.sh</c>.
///
/// This mirrors what <c>Auto-Install.ps1</c> has always done, step for step, because that path is
/// proven and because the guest payload must be identical in every mode (plan §4.1): a LF-normalized
/// copy of the script next to the original, Windows paths mapped to <c>/mnt/…</c> ourselves, and the
/// identity handed over as environment variables in front of <c>bash</c> — never as an inline shell
/// snippet, which is what broke quoting there before.
///
/// Builds are serialized: the LF copy is a single shared file, and one xorriso repack of a
/// multi-gigabyte ISO at a time is what the host can usefully do anyway.
///
/// It implements BOTH halves of the ISO seam, because WSL can do both:
/// <list type="bullet">
/// <item><see cref="IIsoBuilder"/> — per-VM media with the hostname baked in, which is what
/// <see cref="IsoBuildMode.PerVm"/> hands to a VM creation job.</item>
/// <item><see cref="IIsoMediaBuilder"/> — one generic ISO at a path the caller picks, for the catalog.
/// That is what <c>admin iso build</c> drives, as the interactive administrator, and it is how the
/// default <see cref="IsoBuildMode.Prebuilt"/> mode gets anything to consume.</item>
/// </list>
/// The two share everything below the argument vector: the same script, the same source-ISO
/// resolution, the same redaction.
/// </summary>
public sealed class WslIsoBuilder : IIsoBuilder, IIsoMediaBuilder, IDisposable
{
    /// <summary>Copying and repacking a multi-gigabyte ISO, plus a possible download.</summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IProcessRunner _processes;
    private readonly IIsoFileSystem _files;
    private readonly IIsoDownloader _downloader;
    private readonly ConstructdOptions _options;
    private readonly ILogger<WslIsoBuilder> _logger;

    public WslIsoBuilder(
        IProcessRunner processes,
        IIsoFileSystem files,
        IIsoDownloader downloader,
        ConstructdOptions options,
        ILogger<WslIsoBuilder> logger)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _processes = processes;
        _files = files;
        _downloader = downloader;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Per-VM media (<see cref="IsoBuildMode.PerVm"/>): the hostname is baked into the seed, so the
    /// ISO belongs to exactly one VM and is rebuilt for the next one.
    /// </summary>
    public async Task<string> BuildAsync(
        string vmName,
        string seedUser,
        string seedPassword,
        string bootstrapPubKeyPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var name = ArgumentGuard.VmName(vmName);
        var guestHost = name.ToLowerInvariant();
        var cacheDir = ArgumentGuard.WindowsPath(_options.Iso.CacheDir, "Constructd:Iso:CacheDir").TrimEnd('\\', '/');

        var result = await RunBuildAsync(
            vmName: name,
            outputIso: $@"{cacheDir}\{guestHost}-autoinstall.iso",
            // The one difference between the two modes, and it is one environment variable: this ISO
            // knows its guest's name, generic media does not.
            identity: [$"VM_HOST={guestHost}"],
            seedUser,
            seedPassword,
            bootstrapPubKeyPath,
            describing: $"building the autoinstall ISO for {guestHost}",
            describeContents: false,
            progress,
            cancellationToken).ConfigureAwait(false);

        return result.IsoPath;
    }

    /// <summary>
    /// Generic media for the catalog (<see cref="IsoBuildMode.Prebuilt"/>): no hostname is baked in
    /// at all — the guest adopts one at first boot from
    /// <see cref="IsoMediaRequest.HostnameSource"/>, which the build script turns into a one-shot
    /// unit inside the image.
    /// </summary>
    public async Task<IsoMediaResult> BuildMediaAsync(
        IsoMediaRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var output = ArgumentGuard.WindowsPath(request.OutputPath, "autoinstall media path");
        var hostnameSource = ArgumentGuard.EnvironmentValue(
            request.HostnameSource, "Constructd:Iso:HostnameSource");

        return await RunBuildAsync(
            vmName: null,
            outputIso: output,
            identity: [$"VM_HOSTNAME_SOURCE={hostnameSource}"],
            request.SeedUser,
            request.SeedPassword,
            request.BootstrapPublicKeyPath,
            describing: $"building generic autoinstall media (hostname source: {hostnameSource})",
            describeContents: true,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Everything the two modes share: the source ISO, the LF-normalized script copy, the argument
    /// vector, the timeout, the redaction and the checks on the result. Only
    /// <paramref name="identity"/> and where the output goes differ.
    /// </summary>
    private async Task<IsoMediaResult> RunBuildAsync(
        string? vmName,
        string outputIso,
        IReadOnlyList<string> identity,
        string seedUser,
        string seedPassword,
        string bootstrapPubKeyPath,
        string describing,
        bool describeContents,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var name = vmName;

        var scriptsDir = ArgumentGuard.WindowsPath(_options.ScriptsDir, "Constructd:ScriptsDir").TrimEnd('\\', '/');
        var cacheDir = ArgumentGuard.WindowsPath(_options.Iso.CacheDir, "Constructd:Iso:CacheDir").TrimEnd('\\', '/');
        var user = ArgumentGuard.EnvironmentValue(seedUser, "Constructd:Iso:SeedUser");
        var password = ArgumentGuard.EnvironmentValue(seedPassword, "seed password");
        var sourceId = ArgumentGuard.EnvironmentValue(_options.Iso.SourceId, "Constructd:Iso:SourceId");

        // The bootstrap key the client provisioner authenticates with; the checkout's own copy unless
        // the admin pointed somewhere else.
        var pubKey = string.IsNullOrWhiteSpace(bootstrapPubKeyPath)
            ? $@"{scriptsDir}\keys\bootstrap_ed25519.pub"
            : bootstrapPubKeyPath;

        // Nothing that follows may leak the seed password into progress or the log.
        var safeProgress = progress is null ? null : new RedactingProgress(progress, password);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _files.CreateDirectory(cacheDir);

            var sourceIso = await ResolveSourceIsoAsync(name, cacheDir, safeProgress, cancellationToken)
                .ConfigureAwait(false);

            var buildScript = $@"{scriptsDir}\bin\build-autoinstall-iso.sh";
            if (!_files.FileExists(buildScript))
            {
                throw Fail(name, $"the ISO build script is missing: {buildScript}");
            }

            if (!_files.FileExists(pubKey))
            {
                throw Fail(name, $"the bootstrap public key is missing: {pubKey}");
            }

            // A LF-normalized copy next to the original, run directly. Same reason Auto-Install.ps1
            // does it: a CRLF script (or an inline here-string through PowerShell → wsl.exe → bash)
            // mangles quoting and breaks constructs like `trap`.
            var lfScript = $@"{scriptsDir}\bin\.build-autoinstall.lf.sh";
            _files.WriteAllText(lfScript, _files.ReadAllText(buildScript).Replace("\r", string.Empty, StringComparison.Ordinal));

            try
            {
                var arguments = new List<string>();
                if (!string.IsNullOrWhiteSpace(_options.WslDistro))
                {
                    arguments.Add("-d");
                    arguments.Add(ArgumentGuard.Text(_options.WslDistro, "Constructd:WslDistro"));
                }

                arguments.Add("-u");
                arguments.Add("root");
                arguments.Add("--");
                arguments.Add("env");
                arguments.Add($"VM_USER={user}");
                arguments.Add($"VM_PASS={password}");
                arguments.AddRange(identity);
                arguments.Add($"SOURCE_ID={sourceId}");
                arguments.Add($"BOOTSTRAP_PUBKEY_FILE={WslPath.FromWindows(pubKey, "Constructd:Iso:BootstrapPublicKeyPath")}");
                arguments.Add("bash");
                arguments.Add(WslPath.FromWindows(lfScript, "iso build script"));
                arguments.Add(WslPath.FromWindows(sourceIso, "Constructd:Iso:SourcePath"));
                arguments.Add(WslPath.FromWindows(outputIso, "output iso"));

                safeProgress?.Report(describing);

                var result = await _processes.RunAsync(
                    _options.WslPath,
                    arguments,
                    standardInput: null,
                    BuildTimeout,
                    safeProgress,
                    cancellationToken).ConfigureAwait(false);

                if (result.TimedOut)
                {
                    throw Fail(name, $"the WSL build timed out after {BuildTimeout.TotalMinutes:0} minutes");
                }

                if (result.ExitCode != 0)
                {
                    // The exit code, not the build's output: bash and xorriso echo their arguments, and
                    // those arguments include VM_PASS. The build's progress lines are the diagnostic
                    // channel, and they are redacted.
                    throw Fail(name, $"the WSL build exited with {result.ExitCode}");
                }

                if (!_files.FileExists(outputIso) || _files.FileLength(outputIso) <= 0)
                {
                    throw Fail(name, $"the build reported success but {outputIso} is missing or empty");
                }

                safeProgress?.Report($"autoinstall ISO ready: {outputIso}");

                // Only the catalog needs to describe what went in, and hashing a multi-gigabyte ISO
                // is not something a per-VM build should pay for on every creation.
                return describeContents
                    ? new IsoMediaResult(
                        outputIso,
                        sourceIso,
                        _files.ComputeSha256(sourceIso),
                        SshPublicKey.FingerprintOrUnknown(ReadTextOrEmpty(pubKey)),
                        _files.ComputeSha256(buildScript))
                    : new IsoMediaResult(outputIso, sourceIso, string.Empty, string.Empty, string.Empty);
            }
            finally
            {
                // As Auto-Install.ps1 does: the normalized copy is scratch, not state.
                try
                {
                    _files.DeleteFile(lfScript);
                }
                catch (IOException)
                {
                    // Leaving it behind is harmless — the next build overwrites it.
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The Ubuntu ISO to remaster: the admin's own copy, or the configured URL fetched into the cache
    /// once and reused by every later build.
    /// </summary>
    private async Task<string> ResolveSourceIsoAsync(
        string? vmName,
        string cacheDir,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.Iso.SourcePath))
        {
            var configured = ArgumentGuard.WindowsPath(_options.Iso.SourcePath, "Constructd:Iso:SourcePath");
            if (!_files.FileExists(configured) || _files.FileLength(configured) <= 0)
            {
                throw Fail(vmName, $"the configured source ISO is missing or empty: {configured}");
            }

            VerifyChecksum(vmName, configured);
            return configured;
        }

        if (string.IsNullOrWhiteSpace(_options.Iso.SourceUrl))
        {
            throw Fail(vmName, "neither Constructd:Iso:SourcePath nor Constructd:Iso:SourceUrl is configured");
        }

        if (!Uri.TryCreate(_options.Iso.SourceUrl, UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            throw Fail(vmName, "Constructd:Iso:SourceUrl is not an absolute http(s) URL");
        }

        var fileName = ArgumentGuard.Text(
            Path.GetFileName(url.AbsolutePath) is { Length: > 0 } name ? name : "source.iso",
            "Constructd:Iso:SourceUrl file name",
            maxLength: 128);

        if (fileName.Contains('\\', StringComparison.Ordinal) ||
            fileName.Contains('/', StringComparison.Ordinal) ||
            fileName.Contains("..", StringComparison.Ordinal))
        {
            throw Fail(vmName, "Constructd:Iso:SourceUrl does not end in a plain file name");
        }

        var cached = $@"{cacheDir}\{fileName}";

        if (!_files.FileExists(cached) || _files.FileLength(cached) <= 0)
        {
            try
            {
                await _downloader.DownloadAsync(url, cached, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw Fail(vmName, $"downloading the source ISO failed: {SafeError.Describe(ex)}");
            }

            if (!_files.FileExists(cached) || _files.FileLength(cached) <= 0)
            {
                throw Fail(vmName, $"the downloaded source ISO is missing or empty: {cached}");
            }
        }

        VerifyChecksum(vmName, cached);
        return cached;
    }

    /// <summary>
    /// Checked on every use, not only right after the download: a cache entry can be truncated by a
    /// full disk or replaced on a host several people administer.
    /// </summary>
    private void VerifyChecksum(string? vmName, string isoPath)
    {
        var expected = _options.Iso.Sha256?.Trim();
        if (string.IsNullOrEmpty(expected))
        {
            return;
        }

        var actual = _files.ComputeSha256(isoPath);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw Fail(vmName, $"the source ISO does not match Constructd:Iso:Sha256 ({isoPath})");
        }
    }

    /// <summary>
    /// Builds the exception and logs the failure. <paramref name="reason"/> is composed here — from an
    /// exit code, a timeout, or the service's own configuration — and never from the child's output:
    /// WSL's stderr can quote the command line, and the command line carries the seed password.
    /// </summary>
    private IsoBuildException Fail(string? vmName, string reason)
    {
        _logger.LogError("Autoinstall ISO build for {Vm} failed: {Reason}", vmName ?? "generic media", reason);

        return vmName is null ? IsoBuildException.ForMedia(reason) : new IsoBuildException(vmName, reason);
    }

    /// <summary>
    /// The bootstrap key's text, for the fingerprint that goes into the catalog. A key file that
    /// cannot be read is not worth failing a finished build over -- the fingerprint records "unknown".
    /// </summary>
    private string ReadTextOrEmpty(string path)
    {
        try
        {
            return _files.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>
/// Progress that cannot repeat the seed password. The build script does not print it, but its output
/// is job state — durable, streamed over SSE and readable by the VM's owner — so the guarantee is made
/// here rather than assumed of a shell script.
/// </summary>
internal sealed class RedactingProgress(IProgress<string> inner, string secret) : IProgress<string>
{
    public void Report(string value) => inner.Report(Redact(value, secret));

    public static string Redact(string value, string secret) =>
        string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(value)
            ? value
            : value.Replace(secret, "***", StringComparison.Ordinal);
}
