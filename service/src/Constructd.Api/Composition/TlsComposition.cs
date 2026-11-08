using System.Security.Cryptography.X509Certificates;
using Constructd.Core.Configuration;
using Constructd.Core.Logic;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Constructd.Api.Composition;

/// <summary>
/// TLS with the certificate created at service install; the client pins its thumbprint at enrollment
/// (plan §4.4). A thumbprint is resolved from the Windows certificate store, a path from a PFX.
/// After a rename the host can also keep its previous certificate, chosen per handshake by
/// <see cref="TlsCertificateSelection"/>, so clients that pinned it keep working.
/// </summary>
public static class TlsComposition
{
    /// <summary>True when a current certificate is configured. Without one the service runs without TLS settings.</summary>
    public static bool IsConfigured(ConstructdOptions options) =>
        !string.IsNullOrWhiteSpace(options.CertPath) || !string.IsNullOrWhiteSpace(options.CertThumbprint);

    /// <summary>Puts the configured certificate(s) on Kestrel's HTTPS defaults.</summary>
    public static void Apply(HttpsConnectionAdapterOptions https, ConstructdOptions options)
    {
        var current = Load(options.CertPath, options.CertPassword, options.CertThumbprint, "Constructd:CertPath")
            ?? throw new InvalidOperationException("No TLS certificate is configured (Constructd:CertPath or CertThumbprint).");
        var previous = Load(options.PreviousCertPath, options.PreviousCertPassword, options.PreviousCertThumbprint, "Constructd:PreviousCertPath");
        if (previous is null)
        {
            https.ServerCertificate = current;
            return;
        }

        // Kestrel prefers the selector over ServerCertificate; it receives the SNI name, or null/empty without SNI.
        https.ServerCertificateSelector = (_, serverName) => TlsCertificateSelection.Choose(serverName, current, previous);
    }

    private static X509Certificate2? Load(string? path, string? password, string? thumbprint, string setting)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, password);
        }

        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return null;
        }

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        return found.Count > 0
            ? found[0]
            : throw new InvalidOperationException(
                $"No certificate with thumbprint '{thumbprint}' in LocalMachine\\My ({setting.Replace("Path", "Thumbprint", StringComparison.Ordinal)}).");
    }
}
