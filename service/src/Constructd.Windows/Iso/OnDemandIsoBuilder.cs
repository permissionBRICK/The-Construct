using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Logic;

namespace Constructd.Windows.Iso;

/// <summary>Reuse published media, or build it on the host when missing or explicitly refreshed.</summary>
public sealed class OnDemandIsoBuilder(
    IIsoCatalog catalog,
    IIsoMediaBuilder builder,
    IIsoFileSystem files,
    IClock clock,
    ConstructdOptions options) : IIsoBuilder, IDisposable
{
    // Cover selection, build and publication: simultaneous first installs share one build.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> BuildAsync(string vmName, string seedUser, string seedPassword,
        string bootstrapPubKeyPath, IProgress<string>? progress, CancellationToken cancellationToken,
        bool redownload = false)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            progress?.Report("waiting for another ISO build on the host to finish");
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            var current = catalog.GetCurrent();
            if (!redownload && current is { Sidecar: not null } && Matches(current.Sidecar, seedUser, bootstrapPubKeyPath))
            {
                progress?.Report($"using the published autoinstall ISO {current.FileName}");
                return current.Path;
            }

            progress?.Report(redownload
                ? "redownload requested: downloading and patching fresh media on the host"
                : "no matching published media: building the autoinstall ISO on the host");
            var path = catalog.NextMediaPath();
            try
            {
                var media = await builder.BuildMediaAsync(new(path, seedUser, seedPassword,
                    bootstrapPubKeyPath, options.Iso.HostnameSource, redownload), progress, cancellationToken)
                    .ConfigureAwait(false);
                catalog.Publish(media.IsoPath, new(clock.UtcNow, media.SourceIsoPath, media.SourceSha256,
                    seedUser, media.BootstrapKeyFingerprint, options.Iso.HostnameSource, media.BuildScriptSha256));
                return media.IsoPath;
            }
            catch
            {
                // Keep the old published ISO; only discard this failed build's reserved file.
                files.TryDeleteFile(path);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private bool Matches(IsoSidecar sidecar, string user, string keyPath)
    {
        if (sidecar.SeedUser != user || sidecar.HostnameSource != options.Iso.HostnameSource) return false;
        var path = string.IsNullOrWhiteSpace(keyPath) ? options.Iso.BootstrapPublicKeyPath : keyPath;
        if (string.IsNullOrWhiteSpace(path)) path = options.ScriptsDir.TrimEnd('\\', '/') + @"\keys\bootstrap_ed25519.pub";
        try
        {
            return !SshPublicKey.TryFingerprint(files.ReadAllText(path), out var fingerprint)
                   || sidecar.BootstrapKeyFingerprint == fingerprint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true; // Preserve the catalog-only behavior if the configured key cannot be read.
        }
    }

    public void Dispose() => _gate.Dispose();
}
