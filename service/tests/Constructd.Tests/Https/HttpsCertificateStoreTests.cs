using System.Security.Cryptography.X509Certificates;
using Constructd.Api.Https;
using Constructd.Core.Configuration;
using Constructd.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Constructd.Tests.Https;

/// <summary>
/// The host's HTTPS certificate store: what it accepts, what it refuses without touching the installed pair,
/// how it keeps the key private, and that a second process (the admin CLI) can replace the certificate under
/// a running service.
/// </summary>
public sealed class HttpsCertificateStoreTests : IDisposable
{
    private readonly string _directory = TestCertificates.TempDirectory();
    private readonly MutableClock _clock = new() { UtcNow = DateTimeOffset.UtcNow };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private HttpsCertificateStore NewStore() =>
        new(new ConstructdOptions { Https = { CertificateDirectory = _directory } }, _clock, NullLogger<HttpsCertificateStore>.Instance);

    [Fact]
    public void Nothing_is_installed_until_a_certificate_is_imported()
    {
        var store = NewStore();
        Assert.Null(store.Current);
        Assert.Null(store.LoadError);
        Assert.False(store.Remove());
    }

    [Fact]
    public void A_pem_pair_is_validated_described_and_kept_private()
    {
        var store = NewStore();
        var issued = TestCertificates.SelfSigned(["*.vms.example", "vms.example"]);

        var info = store.Import(issued.ChainPem, issued.KeyPem);

        Assert.Equal(issued.Sha256, info.Sha256);
        Assert.Equal(["*.vms.example", "vms.example"], info.DnsNames);
        Assert.Equal(1, info.ChainLength);
        Assert.True(store.Current!.Covers("work-vm.vms.example"));
        Assert.False(store.Current.Covers("a.b.vms.example"));
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(Path.Combine(_directory, HttpsCertificateStore.KeyFile));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }

    [Fact]
    public void The_chain_is_served_leaf_first_whatever_order_it_arrived_in()
    {
        var store = NewStore();
        var issued = TestCertificates.WithIssuer(["*.vms.example"], caFirst: true);

        var info = store.Import(issued.ChainPem, issued.KeyPem);

        Assert.Equal(issued.Sha256, info.Sha256);
        Assert.Equal(2, info.ChainLength);
        Assert.Equal("CN=Test Issuing CA", info.Issuer);
    }

    [Fact]
    public void A_refused_pair_leaves_the_installed_certificate_in_place()
    {
        var store = NewStore();
        var good = TestCertificates.SelfSigned(["*.vms.example"]);
        store.Import(good.ChainPem, good.KeyPem);
        var other = TestCertificates.SelfSigned(["*.vms.example"]);
        var expired = TestCertificates.SelfSigned(["*.vms.example"], notAfter: DateTimeOffset.UtcNow.AddDays(-1), notBefore: DateTimeOffset.UtcNow.AddDays(-10));

        Assert.Contains("does not belong", Assert.Throws<HttpsCertificateException>(() => store.Import(good.ChainPem, other.KeyPem)).Message);
        Assert.Contains("expired", Assert.Throws<HttpsCertificateException>(() => store.Import(expired.ChainPem, expired.KeyPem)).Message);
        Assert.Throws<HttpsCertificateException>(() => store.Import("not pem", good.KeyPem));
        Assert.Throws<HttpsCertificateException>(() => store.Import(good.ChainPem, ""));

        Assert.Equal(good.Sha256, store.Current!.Info.Sha256);
    }

    [Fact]
    public void A_pfx_with_its_chain_is_accepted()
    {
        var store = NewStore();
        var issued = TestCertificates.WithIssuer(["*.vms.example"]);
        using var withKey = X509Certificate2.CreateFromPem(issued.Leaf.ExportCertificatePem(), issued.KeyPem);
        var collection = new X509Certificate2Collection { withKey };
        collection.ImportFromPem(issued.ChainPem.Substring(issued.ChainPem.IndexOf("-----END CERTIFICATE-----", StringComparison.Ordinal) + 25));
        var pfx = collection.Export(X509ContentType.Pkcs12, "secret")!;

        Assert.Throws<HttpsCertificateException>(() => store.ImportPfx(pfx, "wrong"));
        var info = store.ImportPfx(pfx, "secret");

        Assert.Equal(issued.Sha256, info.Sha256);
        Assert.Equal(2, info.ChainLength);
    }

    [Fact]
    public void A_certificate_written_by_another_process_is_picked_up_without_a_restart()
    {
        var service = NewStore();
        var first = TestCertificates.SelfSigned(["*.vms.example"]);
        service.Import(first.ChainPem, first.KeyPem);
        Assert.Equal(first.Sha256, service.Current!.Info.Sha256);

        // The admin CLI is its own process with its own store over the same directory.
        var cli = NewStore();
        var renewed = TestCertificates.SelfSigned(["*.vms.example"]);
        Thread.Sleep(20);
        cli.Import(renewed.ChainPem, renewed.KeyPem);

        Assert.Equal(first.Sha256, service.Current!.Info.Sha256);
        _clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(renewed.Sha256, service.Current!.Info.Sha256);

        Assert.True(cli.Remove());
        _clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Null(service.Current);
    }
}
