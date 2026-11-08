using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Constructd.Api.Composition;
using Constructd.Core.Configuration;
using Constructd.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Api;

/// <summary>
/// Real TLS handshakes against Kestrel configured by <see cref="TlsComposition"/>: which certificate
/// a client with and without SNI is shown, with and without a previous certificate.
/// </summary>
public class TlsCompositionTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("constructd-tls-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string Pfx(string file, string commonName, params string[] names)
    {
        var path = Path.Combine(dir, file);
        File.WriteAllBytes(path, TestCertificates.CreatePfx(commonName, names));
        return path;
    }

    private static string Thumbprint(string pfx) =>
        X509CertificateLoader.LoadPkcs12FromFile(pfx, TestCertificates.Password).GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);

    /// <summary>Starts Kestrel on a loopback port with the options, runs one handshake per target host, returns the SHA-256 thumbprints seen.</summary>
    private static async Task<string[]> HandshakesAsync(ConstructdOptions options, params string[] targetHosts)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https => TlsComposition.Apply(https, options))));
        await using var app = builder.Build();
        app.MapGet("/", () => "ok");
        await app.StartAsync();
        var port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;

        var seen = new List<string>();
        foreach (var target in targetHosts)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            await using var ssl = new SslStream(tcp.GetStream());
            // An IP address as TargetHost sends no SNI, exactly like a client dialling https://<address>:7462.
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = target,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            });
            seen.Add(ssl.RemoteCertificate!.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256));
        }

        await app.StopAsync();
        return seen.ToArray();
    }

    [Fact]
    public async Task Without_a_previous_certificate_every_handshake_gets_the_current_one()
    {
        var current = Pfx("tls.pfx", "construct.example.test", "node1", "construct.example.test");
        var options = new ConstructdOptions { CertPath = current, CertPassword = TestCertificates.Password };

        var seen = await HandshakesAsync(options, "127.0.0.1", "construct.example.test", "node1");

        Assert.All(seen, s => Assert.Equal(Thumbprint(current), s));
    }

    [Fact]
    public async Task After_a_rename_only_the_new_name_gets_the_new_certificate()
    {
        // The current certificate also lists 127.0.0.1 and the previous one does not: had the IP target
        // been sent as SNI, the current certificate would be chosen, so the first handshake proves no SNI.
        var previous = Pfx("tls-previous.pfx", "192.0.2.10", "node1", "192.0.2.10");
        var current = Pfx("tls.pfx", "construct.example.test", "node1", "construct.example.test", "127.0.0.1");
        var options = new ConstructdOptions
        {
            CertPath = current,
            CertPassword = TestCertificates.Password,
            PreviousCertPath = previous,
            PreviousCertPassword = TestCertificates.Password,
        };

        var seen = await HandshakesAsync(options, "127.0.0.1", "construct.example.test", "node1", "elsewhere.example");

        Assert.Equal(
            new[] { Thumbprint(previous), Thumbprint(current), Thumbprint(previous), Thumbprint(previous) },
            seen);
    }

    [Fact]
    public void A_missing_current_certificate_is_refused_rather_than_serving_only_the_previous_one()
    {
        var previous = Pfx("tls-previous.pfx", "127.0.0.1", "127.0.0.1");
        var options = new ConstructdOptions { PreviousCertPath = previous, PreviousCertPassword = TestCertificates.Password };

        Assert.False(TlsComposition.IsConfigured(options));
        Assert.Throws<InvalidOperationException>(() =>
            TlsComposition.Apply(new Microsoft.AspNetCore.Server.Kestrel.Https.HttpsConnectionAdapterOptions(), options));
    }
}
