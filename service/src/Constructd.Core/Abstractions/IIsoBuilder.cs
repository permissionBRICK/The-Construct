namespace Constructd.Core.Abstractions;

/// <summary>
/// Selects or builds the autoinstall ISO on the host. Redownload forces a fresh source fetch
/// and patch before returning media for VM creation.
///
/// The seed password is a secret: it must not appear in an exception, a log line or a process listing.
/// The service does not rely on that either — an exception from this interface is reduced to its type
/// (<see cref="Logic.SafeError"/>) before anything records it, the log included.
/// </summary>
public interface IIsoBuilder
{
    /// <returns>Path to the built ISO, in the host's file-system namespace.</returns>
    Task<string> BuildAsync(
        string vmName,
        string seedUser,
        string seedPassword,
        string bootstrapPubKeyPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool redownload = false);
}
