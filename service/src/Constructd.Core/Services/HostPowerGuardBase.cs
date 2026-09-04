using Constructd.Core.Abstractions;

namespace Constructd.Core.Services;

/// <summary>
/// The state machine every <see cref="IHostPowerGuard"/> shares, kept away from the platform call it
/// wraps: hold at most ONE request, change it only on an actual transition, and release whatever is
/// still held when the service stops.
///
/// It is here rather than in the Windows implementation because it is the half that can be tested
/// anywhere — <c>FakeHostPowerGuard</c> derives from it, so the idempotence, the locking and the
/// release-on-dispose the real guard relies on are exercised by the ordinary test suite.
/// </summary>
public abstract class HostPowerGuardBase : IHostPowerGuard, IDisposable
{
    private readonly object _gate = new();
    private bool _required;
    private bool _disposed;

    /// <summary>Whether a request is held right now (diagnostics and tests).</summary>
    public bool IsRequired
    {
        get { lock (_gate) { return _required; } }
    }

    public void SetRequired(bool required, string reason)
    {
        lock (_gate)
        {
            // After Dispose the process is stopping: taking a request again would leak one.
            if (_disposed || required == _required)
            {
                return;
            }

            // The flag is set only once the platform call has succeeded, so a failure is retried on
            // the next reconcile instead of being remembered as done.
            if (required)
            {
                Acquire(reason);
            }
            else
            {
                Release(reason);
            }

            _required = required;
        }
    }

    /// <summary>Take the platform's power availability request. Called under the lock, never twice in a row.</summary>
    protected abstract void Acquire(string reason);

    /// <summary>Give it back. Called under the lock, only when one is actually held.</summary>
    protected abstract void Release(string reason);

    /// <summary>Platform cleanup after the request has been released (closing a handle, say).</summary>
    protected virtual void DisposeCore()
    {
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // try/finally, and not for tidiness: if the platform refuses the release (a failing
            // PowerClearRequest), the handle must STILL be closed -- this method never runs again,
            // because _disposed is already set. The failure is not swallowed either; it propagates
            // after the cleanup, so a host that cannot let go of the request says so.
            try
            {
                if (_required)
                {
                    _required = false;
                    Release("the service is stopping");
                }
            }
            finally
            {
                DisposeCore();
            }
        }

        GC.SuppressFinalize(this);
    }
}
