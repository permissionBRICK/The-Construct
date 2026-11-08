using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Constructd.Tests.Https;

/// <summary>Certificates made on the fly for the HTTPS forward tests.</summary>
internal static class TestCertificates
{
    public sealed record Issued(string ChainPem, string KeyPem, X509Certificate2 Leaf)
    {
        public string Sha256 => Convert.ToHexString(SHA256.HashData(Leaf.RawData));
    }

    /// <summary>A self-signed server certificate for <paramref name="names"/>.</summary>
    public static Issued SelfSigned(string[] names, DateTimeOffset? notAfter = null, DateTimeOffset? notBefore = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = Request(names, key);
        var leaf = request.CreateSelfSigned(notBefore ?? DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddDays(90));
        return new(leaf.ExportCertificatePem() + "\n", key.ExportPkcs8PrivateKeyPem(), leaf);
    }

    /// <summary>A leaf signed by its own throwaway CA; the chain PEM lists the CA first when <paramref name="caFirst"/>.</summary>
    public static Issued WithIssuer(string[] names, bool caFirst = false)
    {
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=Test Issuing CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(1));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = Request(names, key);
        using var signed = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(60), RandomNumberGenerator.GetBytes(8));
        var leaf = X509CertificateLoader.LoadCertificate(signed.RawData);
        var chain = caFirst
            ? ca.ExportCertificatePem() + "\n" + leaf.ExportCertificatePem() + "\n"
            : leaf.ExportCertificatePem() + "\n" + ca.ExportCertificatePem() + "\n";
        return new(chain, key.ExportPkcs8PrivateKeyPem(), leaf);
    }

    private static CertificateRequest Request(string[] names, ECDsa key)
    {
        var request = new CertificateRequest("CN=" + names[0], key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names) san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        return request;
    }

    public static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "constructd-https-test-" + Guid.NewGuid().ToString("n"));
}
