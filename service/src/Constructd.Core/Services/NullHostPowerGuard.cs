using Constructd.Core.Abstractions;

namespace Constructd.Core.Services;

/// <summary>
/// Does nothing, and says nothing: the guard on a host that has no power availability request to
/// take — every non-Windows host, and fake mode. Registered in place of the Windows implementation
/// so the reconciler above it has no platform branch of its own.
/// </summary>
public sealed class NullHostPowerGuard : IHostPowerGuard
{
    public void SetRequired(bool required, string reason)
    {
    }
}
