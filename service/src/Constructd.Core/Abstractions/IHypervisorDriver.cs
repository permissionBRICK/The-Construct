using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

/// <summary>Capability flags of a driver (plan §4.2).</summary>
public sealed record DriverCapabilities(bool Checkpoints, bool Suspend, bool Console);

/// <summary>
/// The hypervisor seam. This mirrors the PowerShell driver contract of plan §4.2
/// (<c>New-Vm</c>/<c>Remove-Vm</c>/<c>Start-Vm</c>/<c>Stop-Vm</c>/<c>Get-VmState</c>/
/// <c>Get-VmEndpoint</c>/<c>Wait-VmReachable</c> + capability flags); <c>docs/drivers.md</c>, which
/// writes that contract down, lands with the driver extraction batch (B4).
///
/// The Windows implementation (B7) invokes <c>Create-AgentVM.ps1</c> and the Hyper-V cmdlets through
/// PowerShell; a future Proxmox driver maps the same operations onto its REST API. Nothing above
/// this interface may reference Hyper-V, PowerShell or Windows.
///
/// Explicitly NOT part of this contract: ISO building (<see cref="IIsoBuilder"/>), in-guest
/// provisioning (client-side) and client config.
///
/// Implementations should keep secrets out of their exceptions (no command lines with seed
/// credentials), but the service does not rely on it: an exception from this interface is reduced to
/// its type (<see cref="Logic.SafeError"/>) before anything — job state, the audit trail, the database
/// or the log — records it.
/// </summary>
public interface IHypervisorDriver
{
    DriverCapabilities Capabilities { get; }

    /// <summary>Creates the VM and starts the unattended install. Progress lines flow to the job.</summary>
    Task CreateVmAsync(VmDescriptor descriptor, IProgress<string>? progress, CancellationToken cancellationToken);

    /// <summary>Removes the VM including its disk chain. Missing VMs are not an error.</summary>
    Task RemoveVmAsync(string name, IProgress<string>? progress, CancellationToken cancellationToken);

    Task StartAsync(string name, CancellationToken cancellationToken);

    Task StopAsync(string name, CancellationToken cancellationToken);

    /// <summary>Suspends to disk (Hyper-V <c>Save-VM</c>), freeing host RAM. Requires <see cref="DriverCapabilities.Suspend"/>.</summary>
    Task SaveAsync(string name, CancellationToken cancellationToken);

    Task<VmState> GetStateAsync(string name, CancellationToken cancellationToken);

    /// <summary>The driver-native endpoint, or <c>null</c> when the VM has none yet.</summary>
    Task<Endpoint?> GetEndpointAsync(string name, CancellationToken cancellationToken);

    /// <summary>Polls until SSH answers. Returns false on timeout (replaces the raw socket poll in <c>Create-AgentVM.ps1</c>).</summary>
    Task<bool> WaitReachableAsync(string name, TimeSpan timeout, IProgress<string>? progress, CancellationToken cancellationToken);

    /// <summary>Ejects the install ISO once the unattended install is done.</summary>
    Task DetachInstallMediaAsync(string name, CancellationToken cancellationToken);
}
