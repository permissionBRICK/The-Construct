using System.Security.Cryptography.X509Certificates;

namespace Constructd.Core.Logic;

/// <summary>
/// WHICH CERTIFICATE A TLS HANDSHAKE GETS on a host whose public host was renamed.
///
/// <para>Clients pin the SHA-256 fingerprint of the certificate they enrolled with, and a guest checks
/// the service with <c>curl --cacert</c>, which also checks the NAME. A renamed host therefore needs a
/// certificate that names the new public host (for guests that dial it) without taking the old one
/// away from everybody who pinned it. The service can hold both: the current certificate
/// (<c>Constructd:CertPath</c> / <c>CertThumbprint</c>) and the previous one
/// (<c>Constructd:PreviousCertPath</c> / <c>PreviousCertThumbprint</c>), and it picks one per handshake
/// from the name the client asked for (SNI):</para>
/// <list type="bullet">
/// <item>no previous certificate: the current one, always. This is the behaviour without a rename.</item>
/// <item>a name the current certificate covers and the previous one does not: the current one. That
///   client dials the host by its new name.</item>
/// <item>anything else: the previous one. That is no SNI at all (an address-based URL such as
///   <c>https://192.0.2.10:7462</c>, since clients never send an IP address as SNI), a name the
///   previous certificate covers (the old public host, the node name), or a name neither covers. Each
///   of those clients enrolled while the previous certificate was the only one.</item>
/// </list>
/// </summary>
public static class TlsCertificateSelection
{
    /// <summary>The certificate for a handshake that asked for <paramref name="serverName"/> (null: no SNI).</summary>
    public static X509Certificate2 Choose(string? serverName, X509Certificate2 current, X509Certificate2? previous)
    {
        if (previous is null) return current;
        if (string.IsNullOrWhiteSpace(serverName)) return previous;
        var name = serverName.Trim().TrimEnd('.');
        return Names(current, name) && !Names(previous, name) ? current : previous;
    }

    /// <summary>
    /// True when the certificate's subject alternative names cover <paramref name="hostName"/>,
    /// wildcards included. The subject CN is ignored, as browsers and curl ignore it when SANs exist.
    /// </summary>
    public static bool Names(X509Certificate2 certificate, string hostName)
    {
        try
        {
            return certificate.MatchesHostname(hostName, allowWildcards: true, allowCommonName: false);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
