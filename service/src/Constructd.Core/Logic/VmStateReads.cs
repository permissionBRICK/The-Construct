using System.Diagnostics;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

/// <summary>
/// A state read can come back without an answer for a moment while the VM itself is fine. Proxmox
/// resolves the VM through <c>/cluster/resources</c>, pvestatd's cache, which lags a create or delete by
/// up to ten seconds (<see cref="VmState.Absent"/>), and a read that fails is <see cref="VmState.Unknown"/>.
/// A caller that would refuse on either reads again for a while first.
/// </summary>
public static class VmStateReads
{
    /// <summary>How long <see cref="GetSettledStateAsync"/> keeps reading: longer than the cache's lag.</summary>
    public static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The VM's state, read again every half second while it is Unknown or Absent. The window counts from
    /// the first read; a later read still running at its end is cut off, and the last answer stands. The
    /// caller's own cancellation still throws.
    /// </summary>
    public static async Task<VmState> GetSettledStateAsync(this IHypervisorDriver driver, string name, CancellationToken ct, TimeSpan? window = null)
    {
        var settle = window ?? SettleWindow;
        var waited = Stopwatch.StartNew();
        var state = await driver.GetStateAsync(name, ct).ConfigureAwait(false);
        while (state is VmState.Unknown or VmState.Absent && waited.Elapsed < settle)
        {
            var left = settle - waited.Elapsed;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), deadline.Token).ConfigureAwait(false);
                state = await driver.GetStateAsync(name, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break;
            }
        }
        return state;
    }
}
