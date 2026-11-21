using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Logic;
using Constructd.Core.Services;
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

/// <summary>Shared download, checksum, serialization, timeout and provenance rules for ISO builders.</summary>
public abstract class IsoBuilderBase : IIsoBuilder, IIsoMediaBuilder, IDisposable
{
    protected abstract string BuilderName { get; }
    protected abstract string GetBuilderPath(string scriptsDir);
    protected abstract Task<ProcessResult> RunToolAsync(string sourceIso, string outputIso, string builderPath,
        string user, string password, IReadOnlyList<string> identity, string pubKey, string sourceId,
        IProgress<string>? progress, CancellationToken cancellationToken);

    /// <summary>Copying and repacking a multi-gigabyte ISO, plus a possible download.</summary>
    protected static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    protected readonly IProcessRunner _processes;
    protected readonly IIsoFileSystem _files;
    private readonly IIsoDownloader _downloader;
    protected readonly ConstructdOptions _options;
    private readonly ILogger _logger;

    protected IsoBuilderBase(
        IProcessRunner processes,
        IIsoFileSystem files,
        IIsoDownloader downloader,
        ConstructdOptions options,
        ILogger logger)
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
        CancellationToken cancellationToken,
        bool redownload = false)
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
            cancellationToken, redownload).ConfigureAwait(false);

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
            cancellationToken, request.Redownload).ConfigureAwait(false);
    }

    /// <summary>
    /// Everything the strategies share: source resolution, timeout, redaction, provenance,
    /// and output validation. Only
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
        CancellationToken cancellationToken,
        bool redownload)
    {
        var name = vmName;

        // Only the defaults below read it: an install that names the builder and the key (every
        // installer does) needs no scripts directory.
        var scriptsDir = _options.ScriptsDir.TrimEnd('\\', '/');
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

            var sourceIso = await ResolveSourceIsoAsync(name, cacheDir, safeProgress, cancellationToken, redownload)
                .ConfigureAwait(false);

            var buildScript = GetBuilderPath(scriptsDir);
            if (!_files.FileExists(buildScript))
            {
                throw Fail(name, $"the ISO build script is missing: {buildScript}");
            }

            if (!_files.FileExists(pubKey))
            {
                throw Fail(name, $"the bootstrap public key is missing: {pubKey}");
            }

            safeProgress?.Report(describing);
            JobProgressTracker.Current?.Begin("iso:build", "Building image");
            var result = await RunToolAsync(sourceIso, outputIso, buildScript, user, password, identity,
                pubKey, sourceId, safeProgress, cancellationToken).ConfigureAwait(false);

            if (result.TimedOut)
            {
                throw Fail(name, $"the {BuilderName} build timed out after {BuildTimeout.TotalMinutes:0} minutes");
            }

            if (result.ExitCode != 0)
            {
                // The exit code, not the build's output: bash and xorriso echo their arguments, and
                // those arguments include VM_PASS. The build's progress lines are the diagnostic
                // channel, and they are redacted.
                throw Fail(name, $"the {BuilderName} build exited with {result.ExitCode}");
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
            _gate.Release();
        }
    }

    public string? CurrentBuilderSha256()
    {
        string path;
        // No configured builder and no scripts directory to find it in: nothing to compare.
        try { path = GetBuilderPath(_options.ScriptsDir.TrimEnd('\\', '/')); }
        catch (InvalidPlatformArgumentException) { return null; }
        return _files.FileExists(path) ? _files.ComputeSha256(path) : null;
    }

    /// <summary>
    /// The Ubuntu ISO to remaster: the admin's own copy, or the configured URL fetched into the cache
    /// once and reused by every later build. With neither configured (an engine installed for one PC), the current
    /// 24.04 live-server image the release directory names, checked against its SHA256SUMS.
    /// </summary>
    private async Task<string> ResolveSourceIsoAsync(
        string? vmName,
        string cacheDir,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool redownload)
    {
        var configuredUrl = !string.IsNullOrWhiteSpace(_options.Iso.SourceUrl);
        var configuredPath = !string.IsNullOrWhiteSpace(_options.Iso.SourcePath);
        if (redownload && !configuredUrl && configuredPath)
            throw Fail(vmName, "redownload requires Constructd:Iso:SourceUrl; a local SourcePath cannot be downloaded");

        if (!redownload && configuredPath)
        {
            var configured = ArgumentGuard.WindowsPath(_options.Iso.SourcePath, "Constructd:Iso:SourcePath");
            if (!_files.FileExists(configured) || _files.FileLength(configured) <= 0)
            {
                throw Fail(vmName, $"the configured source ISO is missing or empty: {configured}");
            }

            progress?.Report($"using the configured Ubuntu source ISO on the host: {configured}");
            VerifyChecksum(vmName, configured, _options.Iso.Sha256, "Constructd:Iso:Sha256", progress);
            return configured;
        }

        if (!configuredUrl)
        {
            return await CurrentReleaseAsync(vmName, cacheDir, progress, cancellationToken, redownload).ConfigureAwait(false);
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

        return await CachedOrDownloadedAsync(vmName, url, $@"{cacheDir}\{fileName}", _options.Iso.Sha256, "Constructd:Iso:Sha256",
            progress, cancellationToken, redownload).ConfigureAwait(false);
    }

    /// <summary>
    /// No source configured: the current live-server image of the 24.04 line, as its release directory's SHA256SUMS
    /// names it, downloaded once and checked against that list. Images of earlier point releases in the cache go once
    /// the new one is in place. When the list cannot be read, the newest image an earlier lookup kept is used.
    /// </summary>
    private async Task<string> CurrentReleaseAsync(string? vmName, string cacheDir, IProgress<string>? progress,
        CancellationToken cancellationToken, bool redownload)
    {
        progress?.Report($"no Ubuntu source ISO is configured: looking up the current {UbuntuIsoIndex.Line} live-server image on {UbuntuIsoIndex.Directory.Host}");
        (string FileName, string Sha256)? current = null;
        string? lookupFailure = null;
        try
        {
            current = UbuntuIsoIndex.Newest(await _downloader.ReadTextAsync(UbuntuIsoIndex.Sums, cancellationToken).ConfigureAwait(false));
            lookupFailure = current is null ? "SHA256SUMS names no live-server image" : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lookupFailure = SafeError.Describe(ex);
        }

        // A Windows path, whatever the platform the code runs on.
        static string NameOf(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
        var kept = _files.ListFiles(cacheDir, UbuntuIsoIndex.CachePattern)
            .Where(f => UbuntuIsoIndex.PointRelease(NameOf(f)) >= 0 && _files.FileLength(f) > 0)
            .OrderBy(f => UbuntuIsoIndex.PointRelease(NameOf(f))).ToList();
        if (current is not { } image)
        {
            if (kept.Count > 0 && !redownload)
            {
                progress?.Report($"the Ubuntu release list could not be read ({lookupFailure}); using the image kept on the host: {kept[^1]}");
                return kept[^1];
            }

            throw Fail(vmName, $"no Ubuntu source ISO is configured and the current {UbuntuIsoIndex.Line} image could not be looked up on " +
                $"{UbuntuIsoIndex.Directory.Host} ({lookupFailure}); set Constructd:Iso:SourceUrl and Constructd:Iso:Sha256, or Constructd:Iso:SourcePath");
        }

        progress?.Report($"the current {UbuntuIsoIndex.Line} image is {image.FileName}");
        var path = await CachedOrDownloadedAsync(vmName, new Uri(UbuntuIsoIndex.Directory, image.FileName), $@"{cacheDir}\{image.FileName}",
            image.Sha256, "the release's SHA256SUMS", progress, cancellationToken, redownload).ConfigureAwait(false);
        foreach (var older in kept.Where(f => !string.Equals(f, path, StringComparison.OrdinalIgnoreCase)))
        {
            if (_files.TryDeleteFile(older)) progress?.Report($"removed the image of an earlier point release: {older}");
        }

        return path;
    }

    /// <summary>The source ISO in the cache, downloaded first when it is not there (or a redownload asks for it), and checked.</summary>
    private async Task<string> CachedOrDownloadedAsync(string? vmName, Uri url, string cached, string? expectedSha256, string checksumSource,
        IProgress<string>? progress, CancellationToken cancellationToken, bool redownload)
    {
        if (redownload || !_files.FileExists(cached) || _files.FileLength(cached) <= 0)
        {
            progress?.Report(redownload
                ? "refreshing the Ubuntu source ISO in the host cache"
                : $"Ubuntu source ISO is not cached on the host: {cached}");
            var pending = cached + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                JobProgressTracker.Current?.Begin("iso:download", "Downloading ISO");
                await _downloader.DownloadAsync(url, pending, progress, cancellationToken).ConfigureAwait(false);
                if (!_files.FileExists(pending) || _files.FileLength(pending) <= 0)
                    throw Fail(vmName, "the downloaded source ISO is missing or empty");
                VerifyChecksum(vmName, pending, expectedSha256, checksumSource, progress);
                _files.MoveFile(pending, cached, overwrite: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not IsoBuildException)
            {
                throw Fail(vmName, $"downloading the source ISO failed: {SafeError.Describe(ex)}");
            }
            finally
            {
                _files.TryDeleteFile(pending);
                _files.TryDeleteFile(pending + ".part");
            }

            if (!_files.FileExists(cached) || _files.FileLength(cached) <= 0)
            {
                throw Fail(vmName, $"the downloaded source ISO is missing or empty: {cached}");
            }
        }
        else
        {
            progress?.Report($"using the cached Ubuntu source ISO on the host: {cached}");
            VerifyChecksum(vmName, cached, expectedSha256, checksumSource, progress);
        }
        return cached;
    }

    /// <summary>
    /// Checked on every use, not only right after the download: a cache entry can be truncated by a
    /// full disk or replaced on a host several people administer.
    /// </summary>
    private void VerifyChecksum(string? vmName, string isoPath, string? expectedSha256, string checksumSource, IProgress<string>? progress)
    {
        var expected = expectedSha256?.Trim();
        if (string.IsNullOrEmpty(expected))
        {
            return;
        }

        progress?.Report("verifying the Ubuntu source ISO SHA256 on the host");
        var actual = _files.ComputeSha256(isoPath);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw Fail(vmName, $"the source ISO does not match {checksumSource} ({isoPath})");
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
