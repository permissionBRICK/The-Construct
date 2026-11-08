using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Constructd.Tests.Support;

/// <summary>Self-signed certificates with explicit subject alternative names, as the host installers make them.</summary>
public static class TestCertificates
{
    public const string Password = "test";

    /// <summary>A certificate whose SANs are <paramref name="names"/>: IP addresses become IP entries, the rest DNS entries.</summary>
    public static X509Certificate2 Create(string commonName, params string[] names) =>
        X509CertificateLoader.LoadPkcs12(CreatePfx(commonName, names), Password);

    /// <summary>The same as <see cref="Create"/>, as PFX bytes protected with <see cref="Password"/>.</summary>
    public static byte[] CreatePfx(string commonName, params string[] names)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            if (IPAddress.TryParse(name, out var address)) san.AddIpAddress(address);
            else san.AddDnsName(name);
        }

        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return certificate.Export(X509ContentType.Pfx, Password);
    }
}
