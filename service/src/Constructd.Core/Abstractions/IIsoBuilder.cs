namespace Constructd.Core.Abstractions;

/// <summary>
/// Builds the autoinstall ISO. Backend-agnostic by design: the Windows implementation shells out to
/// the proven <c>bin/build-autoinstall-iso.sh</c> through <c>wsl.exe</c> with the same
/// <c>VM_USER</c>/<c>VM_PASS</c>/<c>VM_HOST</c> + bootstrap-pubkey env contract (plan §3.2, §4.4).
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
        CancellationToken cancellationToken);
}
