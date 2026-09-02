using System.Globalization;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Logic;
using Microsoft.Extensions.Logging;

namespace Constructd.Windows.Iso;

/// <summary>
/// There is no install media to boot a VM from. The message is the remedy: this reaches the job
/// error, the audit trail and the API response, and the person who sees it is the one who has to run
/// the command.
/// </summary>
public sealed class IsoNotBuiltException(string reason, string command)
    : Exception($"No autoinstall ISO is available on this host: {reason}. Build it as an administrator: {command}"),
      IConstructdError
{
    /// <summary>The exact command line to run, so callers can print it on its own.</summary>
    public string Command { get; } = command;
}

/// <summary>
/// The CONSUMING half of the ISO seam in <see cref="IsoBuildMode.Prebuilt"/> mode: it does not build
/// anything, it hands back the media an administrator already published into the
/// <see cref="IIsoCatalog"/>.
///
/// This is the default because of a field finding (2026-09-02, plan §4.10): <c>wsl.exe</c> refuses to
/// run as LocalSystem (<c>WSL_E_LOCAL_SYSTEM_NOT_SUPPORTED</c>), and LocalSystem is the identity the
/// service runs as. So the ISO is built once, interactively, by the administrator's own WSL, and the
/// service only consumes it. It is a stopgap by design — service/README.md ("ISO build strategies")
/// lists the ones that replace it
/// (Native, InGuest, HypervisorHost); this class knows about none of them, or about the WSL builder.
///
/// The media it returns is GENERIC: one ISO serves every VM, and the guest adopts its hostname from
/// the hypervisor at first boot.
/// </summary>
public sealed class PrebuiltIsoBuilder(
    IIsoCatalog catalog,
    IIsoFileSystem files,
    ConstructdOptions options,
    ILogger<PrebuiltIsoBuilder> logger) : IIsoBuilder
{
    private readonly IIsoCatalog _catalog = catalog;
    private readonly IIsoFileSystem _files = files;
    private readonly ConstructdOptions _options = options;
    private readonly ILogger<PrebuiltIsoBuilder> _logger = logger;

    /// <summary>
    /// The command an administrator runs to produce what this class consumes. Printed in the
    /// exception, by the admin CLI and by the installer, so it is written down once.
    /// </summary>
    public const string BuildCommand = "constructd admin iso build";

    /// <summary>
    /// <paramref name="vmName"/> and <paramref name="seedPassword"/> are deliberately IGNORED: the
    /// media is generic, so there is nothing per-VM to bake in, and there is no build to hand a
    /// password to. They stay in the signature because the seam is shared with the per-VM strategies
    /// (<c>WslIsoBuilder</c> today) and <c>VmJobs</c> must not care which one is configured. The
    /// guest's identity comes from the hypervisor's own channel instead — see
    /// <see cref="IsoOptions.HostnameSource"/>.
    /// </summary>
    public Task<string> BuildAsync(
        string vmName,
        string seedUser,
        string seedPassword,
        string bootstrapPubKeyPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entry = _catalog.GetCurrent()
                    ?? throw Fail("none has been built yet, or the current one is gone");

        // A sidecar is not decoration: it is the only record of what this ISO contains, and the check
        // below (the bootstrap key) depends on it. Media nobody can describe is not media to install
        // a host's VMs from, and rebuilding is one command.
        if (entry.Sidecar is null)
        {
            throw Fail($"{entry.FileName} has no readable sidecar, so what it contains is unknown");
        }

        var sidecar = entry.Sidecar;
        progress?.Report(
            $"using the pre-built autoinstall ISO {entry.FileName} " +
            $"(built {sidecar.BuiltAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)}, " +
            $"from {sidecar.SourceIso}, hostname source {sidecar.HostnameSource}, " +
            $"bootstrap key {sidecar.BootstrapKeyFingerprint})");

        WarnOnKeyMismatch(sidecar, bootstrapPubKeyPath, progress);

        // Said out loud once per creation: it is the difference between "this VM will be called what I
        // asked for" and a support question about a guest named construct-seed.
        progress?.Report(
            $"the guest takes its hostname from the hypervisor at first boot (source: {sidecar.HostnameSource})");

        return Task.FromResult(entry.Path);
    }

    /// <summary>
    /// A rotated bootstrap key with stale media is the failure that looks like success: the VM
    /// installs, boots, and then refuses the client's key. Reported rather than fatal — the sidecar
    /// records what a past build saw, and an admin who knows better should not be blocked by it.
    /// </summary>
    private void WarnOnKeyMismatch(IsoSidecar sidecar, string bootstrapPubKeyPath, IProgress<string>? progress)
    {
        var path = string.IsNullOrWhiteSpace(bootstrapPubKeyPath)
            ? _options.Iso.BootstrapPublicKeyPath
            : bootstrapPubKeyPath;

        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sidecar.BootstrapKeyFingerprint))
        {
            return;
        }

        string text;
        try
        {
            text = _files.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return;
        }

        if (!SshPublicKey.TryFingerprint(text, out var fingerprint) ||
            string.Equals(fingerprint, sidecar.BootstrapKeyFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        var warning =
            $"WARNING: this host's bootstrap key is {fingerprint}, but the media carries " +
            $"{sidecar.BootstrapKeyFingerprint} — the new VM will refuse the client's key. " +
            $"Rebuild the media: {BuildCommand} --force";

        _logger.LogWarning(
            "The configured bootstrap key {Configured} differs from the one in the current media {Media}.",
            fingerprint,
            sidecar.BootstrapKeyFingerprint);

        progress?.Report(warning);
    }

    private IsoNotBuiltException Fail(string reason)
    {
        _logger.LogError("No usable pre-built autoinstall ISO: {Reason}.", reason);

        return new IsoNotBuiltException(reason, BuildCommand);
    }
}
