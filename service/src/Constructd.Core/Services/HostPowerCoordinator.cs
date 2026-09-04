using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Logic;

namespace Constructd.Core.Services;

/// <summary>
/// Turns "which VMs are running" into the host's power availability request (plan §4.13). It owns no
/// poller: the idle scheduler already refreshes every VM's state from the hypervisor once a tick and
/// writes it to the registry, so this reads that registry straight afterwards. One source of truth,
/// one poll.
///
/// With <c>Constructd:Power:KeepHostAwake</c> off it does nothing at all — the guard is never even
/// asked, so no request is ever taken on a host that opted out.
/// </summary>
public sealed class HostPowerCoordinator(
    IVmRepository vms,
    IHostPowerGuard guard,
    PowerOptions options)
{
    /// <summary>
    /// Brings the request in line with the registry. Returns what was asked for, or <c>null</c> when
    /// the feature is switched off.
    /// </summary>
    public async Task<HostPowerRequest?> ReconcileAsync(CancellationToken cancellationToken)
    {
        if (!options.KeepHostAwake)
        {
            return null;
        }

        var all = await vms.ListAsync(owner: null, cancellationToken).ConfigureAwait(false);
        var request = HostPowerPlanner.Plan(all.Select(vm => vm.State));

        // Idempotent by contract: only a transition reaches the platform.
        guard.SetRequired(request.Required, request.Reason);

        return request;
    }
}
