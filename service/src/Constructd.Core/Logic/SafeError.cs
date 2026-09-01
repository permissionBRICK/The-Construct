namespace Constructd.Core.Logic;

/// <summary>
/// Marks an exception whose message this service composed itself and can therefore repeat in durable
/// state: job errors, progress lines, audit details. Implemented by the service's own exception types.
/// </summary>
public interface IConstructdError
{
}

/// <summary>
/// Turns an exception into something safe to persist.
///
/// An exception from a dependency is reduced to its type name: a driver that shells out to PowerShell
/// or to <c>build-autoinstall-iso.sh</c> can easily put a whole command line — including the VM's seed
/// password — into its message, its stack trace, its <c>Data</c> or an inner exception.
///
/// This is the service's own boundary, not a request on plug-in implementations: the reduced form is
/// what reaches job state, the SSE stream, the audit trail, the database <em>and the log</em>. The
/// exception object itself is never handed to a logger, because a rendered log entry includes all of
/// those fields. Implementations are asked to keep secrets out of exceptions as well, but nothing
/// depends on them doing so.
/// </summary>
public static class SafeError
{
    /// <summary>The description that may be persisted, streamed or audited.</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            IConstructdError => exception.Message,
            OperationCanceledException => "cancelled",
            _ => exception.GetType().Name,
        };
    }
}

/// <summary>Raised when a VM does not answer SSH within the configured window.</summary>
public sealed class VmNotReachableException(string vmName, TimeSpan timeout)
    : Exception($"VM '{vmName}' did not answer ssh within {timeout.TotalMinutes:0} minutes."), IConstructdError
{
    public string VmName { get; } = vmName;
}
