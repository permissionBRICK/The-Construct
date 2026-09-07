using System.Net;
using System.Net.Sockets;
using Constructd.Core.Domain;
namespace Constructd.Core.Logic;

/// <summary>Accident prevention only; KVP and ARP cannot prove IP ownership.</summary>
public static class GuestAddressRules
{
    public static IPAddress? Parse(string value) => IPAddress.TryParse(value, out var ip) &&
        (ip.AddressFamily == AddressFamily.InterNetwork || ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId == 0) ? ip : null;

    public static bool Usable(IPAddress ip, IReadOnlyList<IPAddress> hostAddresses,
        IReadOnlyList<GuestAdapter> child, IReadOnlyList<GuestAdapter> via, IReadOnlyList<GuestSubnet> subnets)
    {
        var bytes = ip.GetAddressBytes();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) ||
            ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv4MappedToIPv6 || hostAddresses.Contains(ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork && (bytes[0] == 0 || bytes[0] >= 224 || bytes[0] == 169 && bytes[1] == 254)) return false;
        return subnets.Any(subnet => child.Any(a => Same(a.SwitchName, subnet.SwitchName)) &&
            via.Any(a => Same(a.SwitchName, subnet.SwitchName)) && InSubnet(ip, subnet.Cidr));
    }
    private static bool Same(string? a, string b) => a is not null && StringComparer.OrdinalIgnoreCase.Equals(a, b);
    public static bool InSubnet(IPAddress ip, string cidr)
    {
        var pieces = cidr.Split('/');
        if (pieces.Length != 2 || Parse(pieces[0]) is not { } baseIp || !int.TryParse(pieces[1], out var prefix) || baseIp.AddressFamily != ip.AddressFamily) return false;
        var bytes = ip.GetAddressBytes(); var baseBytes = baseIp.GetAddressBytes();
        if (prefix < 0 || prefix > bytes.Length * 8) return false;
        for (var bit = 0; bit < prefix; bit++)
            if ((bytes[bit / 8] & (128 >> (bit % 8))) != (baseBytes[bit / 8] & (128 >> (bit % 8)))) return false;
        if (ip.AddressFamily != AddressFamily.InterNetwork || prefix >= 31) return true;
        var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var mask = uint.MaxValue >> prefix;
        return (value & mask) != mask && (value & mask) != 0;
    }
}
