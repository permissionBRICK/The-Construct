using System.Net;
using System.Net.Sockets;

namespace Constructd.Core.Logic;

/// <summary>
/// Starts TCP listeners in dual-stack mode (IPv6 + IPv4) when bound to a wildcard address
/// (<see cref="IPAddress.Any"/> or <see cref="IPAddress.IPv6Any"/>) on an OS with IPv6 support, falling back
/// to IPv4-only if the kernel refuses IPv6 sockets. Specific interface or loopback addresses bind verbatim.
/// </summary>
public static class DualStackListener
{
    /// <summary>
    /// Parses <c>Constructd:ListenAddress</c>: an IPv4 or IPv6 literal, or <c>*</c> (treated as wildcard
    /// <see cref="IPAddress.Any"/>, which binds dual-stack when IPv6 is available).
    /// </summary>
    public static IPAddress ParseListenAddress(string? value)
    {
        var trimmed = value?.Trim();
        if (string.Equals(trimmed, "*", StringComparison.Ordinal)) return IPAddress.Any;
        if (IPAddress.TryParse(trimmed, out var address)) return address;
        throw new InvalidOperationException("Constructd:ListenAddress must be an IPv4 or IPv6 address (or '*').");
    }

    public static TcpListener Start(IPAddress address, int port, int? backlog = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        if ((address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) && Socket.OSSupportsIPv6)
        {
            TcpListener? dual = null;
            try
            {
                dual = TcpListener.Create(port);
                if (backlog is int b) dual.Start(b); else dual.Start();
                return dual;
            }
            catch (SocketException ex) when (address.Equals(IPAddress.Any) &&
                ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported or SocketError.AddressAlreadyInUse)
            {
                dual?.Stop();
                // Fall back to IPv4-only when IPv6 is unavailable or an existing IPv6-only service owns the port.
            }
            catch
            {
                dual?.Stop();
                throw;
            }
        }

        var listener = new TcpListener(address, port);
        try
        {
            if (backlog is int b) listener.Start(b); else listener.Start();
            return listener;
        }
        catch
        {
            listener.Stop();
            throw;
        }
    }

    public static bool CanBind(IPAddress address, int port, bool exclusiveAddressUse = false)
    {
        ArgumentNullException.ThrowIfNull(address);
        if ((address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) && Socket.OSSupportsIPv6)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                socket.DualMode = true;
                if (exclusiveAddressUse) socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                return true;
            }
            catch (SocketException ex) when (address.Equals(IPAddress.Any) &&
                ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
            {
                // Fall through to IPv4 probe.
            }
            catch (SocketException)
            {
                return false;
            }
        }

        try
        {
            using var fallback = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            if (exclusiveAddressUse) fallback.ExclusiveAddressUse = true;
            fallback.Bind(new IPEndPoint(address, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
