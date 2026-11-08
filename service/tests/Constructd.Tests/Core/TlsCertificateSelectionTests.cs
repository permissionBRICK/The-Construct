using System.Security.Cryptography.X509Certificates;
using Constructd.Core.Logic;
using Constructd.Tests.Support;

namespace Constructd.Tests.Core;

/// <summary>
/// The certificate a handshake gets after the host's public host was renamed from an address to a
/// name. Everyone who enrolled before the rename pinned the previous certificate, so only a client
/// that asks for the new name may get the new one.
/// </summary>
public class TlsCertificateSelectionTests : IDisposable
{
    // As install-construct-host.sh issues them: SAN = node name plus the public host.
    private readonly X509Certificate2 previous = TestCertificates.Create("192.0.2.10", "node1", "192.0.2.10");
    private readonly X509Certificate2 current = TestCertificates.Create("construct.example.test", "node1", "construct.example.test");

    public void Dispose()
    {
        previous.Dispose();
        current.Dispose();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("construct.example.test")]
    [InlineData("node1")]
    public void Without_a_previous_certificate_everyone_gets_the_current_one(string? serverName) =>
        Assert.Same(current, TlsCertificateSelection.Choose(serverName, current, null));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_handshake_without_SNI_gets_the_previous_certificate(string? serverName) =>
        Assert.Same(previous, TlsCertificateSelection.Choose(serverName, current, previous));

    [Theory]
    [InlineData("construct.example.test")]
    [InlineData("CONSTRUCT.Example.Test")]
    [InlineData("construct.example.test.")]
    public void The_new_name_gets_the_current_certificate(string serverName) =>
        Assert.Same(current, TlsCertificateSelection.Choose(serverName, current, previous));

    [Theory]
    [InlineData("node1")]               // both certificates name the node: it was pinned before the rename
    [InlineData("elsewhere.example")]   // neither names it: only the previous certificate existed back then
    public void Names_the_current_certificate_does_not_own_get_the_previous_one(string serverName) =>
        Assert.Same(previous, TlsCertificateSelection.Choose(serverName, current, previous));

    [Fact]
    public void A_wildcard_in_the_current_certificate_covers_its_names()
    {
        using var wildcard = TestCertificates.Create("hosts.example.test", "*.hosts.example.test");
        Assert.Same(wildcard, TlsCertificateSelection.Choose("construct.hosts.example.test", wildcard, previous));
        Assert.Same(previous, TlsCertificateSelection.Choose("a.b.hosts.example.test", wildcard, previous));
    }

    [Fact]
    public void The_common_name_alone_does_not_name_a_host()
    {
        using var cnOnly = TestCertificates.Create("cn-only.example.test", "other.example.test");
        Assert.False(TlsCertificateSelection.Names(cnOnly, "cn-only.example.test"));
        Assert.True(TlsCertificateSelection.Names(cnOnly, "other.example.test"));
    }
}
