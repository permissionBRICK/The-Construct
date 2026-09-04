namespace Constructd.Core.Abstractions;

/// <summary>
/// Keeps the HOST from going to sleep while the service has VMs running (plan §4.13, "Host stays
/// awake while VMs run"): the field host entered S3 overnight with VMs that were expected to keep
/// serving.
///
/// One seam, one method, no lifetime in the contract: the caller states what it needs
/// ("required, because 3 VM(s) are running") and the implementation makes that true — idempotently,
/// so calling it every tick with the same answer costs nothing. The Windows implementation holds a
/// single power availability request for the service's lifetime; off Windows (and in fake mode) a
/// no-op stands in, which is why nothing above this interface knows what a power request is.
/// </summary>
public interface IHostPowerGuard
{
    /// <summary>
    /// Acquire (or release) the host's power availability request.
    /// <paramref name="reason"/> is a human-readable explanation for the log — never a secret.
    /// </summary>
    void SetRequired(bool required, string reason);
}
