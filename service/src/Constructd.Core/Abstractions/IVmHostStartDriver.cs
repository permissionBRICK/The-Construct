namespace Constructd.Core.Abstractions;

/// <summary>
/// Whether the host brings a VM back when the host itself boots (Hyper-V's automatic start action,
/// Proxmox's <c>onboot</c>). A backend that does not run the VMs (external machines) has no such setting
/// and does not implement this.
/// </summary>
public interface IVmHostStartDriver
{
    /// <summary>
    /// Enabled: a host boot starts (or resumes) the VM when it was running or saved as the host went down;
    /// disabled: a host boot leaves it as it is. Works in every power state and changes nothing else; idempotent.
    /// </summary>
    Task SetStartWithHostAsync(string name, bool enabled, CancellationToken ct);
}
