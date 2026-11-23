namespace Constructd.Core.Abstractions;

/// <summary>
/// Whether a solo engine's API port is reachable from the PC's private network, for pairing a second
/// device (design 5.2, 8.3). Off after the install: the port is open to the PC's own VMs only. The
/// state lives where it takes effect (Windows: one inbound firewall rule for the private profile), so
/// there is nothing to keep in step.
/// </summary>
public interface ILanAccess
{
    Task<bool> GetAsync(CancellationToken ct);

    Task SetAsync(bool enabled, CancellationToken ct);
}
