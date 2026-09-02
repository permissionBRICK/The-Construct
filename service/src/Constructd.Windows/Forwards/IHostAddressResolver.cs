using System.Net;
using System.Net.Sockets;

namespace Constructd.Windows.Forwards;

/// <summary>
/// Resolves the driver's endpoint host to the VM's IPv4 address, which is what a portproxy rule needs
/// as its <c>connectaddress</c>.
///
/// Resolved at apply time rather than stored, because it changes: a VM on the Default Switch gets its
/// address from Hyper-V's NAT DHCP, and a reboot of host or guest can hand out a different one. That
/// churn is the reason <see cref="NetshPortForwardManager.ReconcileAsync"/> exists.
/// </summary>
public interface IHostAddressResolver
{
    /// <summary>The first IPv4 address of a host name, or null when it does not resolve.</summary>
    Task<IPAddress?> ResolveIPv4Async(string host, CancellationToken cancellationToken);
}

/// <summary>DNS (which for a local Hyper-V VM is the Default Switch's NAT resolver).</summary>
public sealed class DnsHostAddressResolver : IHostAddressResolver
{
    public async Task<IPAddress?> ResolveIPv4Async(string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        // An endpoint that is already a literal needs no lookup.
        if (IPAddress.TryParse(host, out var literal))
        {
            return literal.AddressFamily == AddressFamily.InterNetwork ? literal : null;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken)
                .ConfigureAwait(false);

            return addresses.Length > 0 ? addresses[0] : null;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
