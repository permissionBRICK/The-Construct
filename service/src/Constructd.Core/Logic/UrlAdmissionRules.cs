using System.Net;
using System.Net.Sockets;
using Constructd.Core.Abstractions;
namespace Constructd.Core.Logic;

/// <summary>Pure pre-connect rules. A transport must apply them again at every redirect and pin DNS.</summary>
public sealed class UrlAdmissionRules : IUrlAdmissionPolicy
{
    public UrlAdmission Check(Uri url, IReadOnlyList<IPAddress> resolved, bool allowHttp, bool hasChecksum)
    {
        UrlAdmission Deny(string reason, string? address = null) => new(false, reason, address, null, []);
        if (!url.IsAbsoluteUri || (url.Scheme != "https" && !(url.Scheme == "http" && allowHttp && hasChecksum))) return Deny("scheme");
        if (url.UserInfo.Length != 0) return Deny("credentials");
        if (resolved.Count == 0) return Deny("address");
        var addresses = IPAddress.TryParse(url.Host.Trim('[', ']'), out var literal) ? resolved.Append(literal) : resolved;
        foreach (var address in addresses) if (!Public(address)) return Deny("address", address.ToString());
        return new(true, null, null, new UriBuilder(url) { Fragment = "" }.Uri, resolved.Select(a => a.ToString()).ToArray());
    }
    private static bool Public(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;
        if (address.IsIPv4MappedToIPv6) return Public(address.MapToIPv4());
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 169 && b[1] == 254 ||
                b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && (b[1] == 168 || b[1] == 0 && b[2] == 0) ||
                b[0] == 198 && b[1] is 18 or 19 || b[0] == 100 && b[1] is >= 64 and <= 127);
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.ScopeId != 0) return false;
        if (b.Take(12).All(x => x == 0)) return Public(new IPAddress(b.Skip(12).ToArray()));
        return !(address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
            b[0] == 0xff || (b[0] & 0xfe) == 0xfc ||
            b[0] == 0 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Skip(4).Take(8).All(x => x == 0));
    }
}
