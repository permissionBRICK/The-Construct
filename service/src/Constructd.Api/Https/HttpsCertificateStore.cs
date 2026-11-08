using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Logic;

namespace Constructd.Api.Https;

/// <summary>What an admin sees about the installed HTTPS certificate. Never carries key material.</summary>
public sealed record HttpsCertificateInfo(
    string Subject,
    string Issuer,
    IReadOnlyList<string> DnsNames,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string Sha256,
    int ChainLength,
    DateTimeOffset InstalledAt);

/// <summary>A certificate the store refused, with the reason an admin can act on.</summary>
public sealed class HttpsCertificateException(string message) : Exception(message), IConstructdError;

/// <summary>The loaded certificate: what TLS handshakes use, and its description.</summary>
public sealed record HttpsCertificate(SslStreamCertificateContext Context, X509Certificate2 Leaf, HttpsCertificateInfo Info)
{
    /// <summary>True when the certificate's names cover <paramref name="hostName"/> (wildcards included).</summary>
    public bool Covers(string hostName) => Names(Leaf, hostName);

    internal static bool Names(X509Certificate2 certificate, string hostName)
    {
        try
        {
            return certificate.MatchesHostname(hostName.Trim().TrimEnd('.'), allowWildcards: true, allowCommonName: false);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// THE HOST'S HTTPS CERTIFICATE (docs/https-forwards.md): one certificate chain and its private key, kept
/// in two PEM files that only the service account can read, in <c>Constructd:Https:CertificateDirectory</c>
/// (default: <c>https</c> next to the database). The key never leaves the host: guests only ever see the
/// TLS handshakes made with it.
///
/// <para>The API, <c>constructd admin https import</c> and the host administration UI all write through
/// <see cref="Import"/>, which validates before it replaces anything and writes each file atomically.
/// The running service notices a change made by another process (the admin CLI) at the next handshake
/// after at most <see cref="RecheckInterval"/>, so a renewal script needs no restart and no API token.</para>
/// </summary>
public sealed class HttpsCertificateStore
{
    public const string ChainFile = "certificate.pem";
    public const string KeyFile = "private-key.pem";

    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(5);

    private readonly string _directory;
    private readonly IClock _clock;
    private readonly ILogger<HttpsCertificateStore> _logger;
    private readonly object _gate = new();
    private HttpsCertificate? _current;
    private (DateTime Chain, DateTime Key) _stamp;
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;
    private string? _loadError;

    public HttpsCertificateStore(ConstructdOptions options, IClock clock, ILogger<HttpsCertificateStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _directory = options.Https.ResolveCertificateDirectory(options.DatabasePath);
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Where the two files live.</summary>
    public string Directory => _directory;

    /// <summary>Why the files on disk could not be loaded, or null.</summary>
    public string? LoadError
    {
        get
        {
            _ = Current;
            return _loadError;
        }
    }

    /// <summary>The certificate in effect, or null when none is installed (or the files are unreadable).</summary>
    public HttpsCertificate? Current
    {
        get
        {
            var now = _clock.UtcNow;
            lock (_gate)
            {
                if (now - _checkedAt < RecheckInterval) return _current;
                _checkedAt = now;
                var stamp = Stamp();
                if (stamp == _stamp) return _current;
                _stamp = stamp;
                if (stamp == default)
                {
                    _current = null;
                    _loadError = null;
                    return null;
                }

                try
                {
                    _current = Load(File.ReadAllText(ChainPath), File.ReadAllText(KeyPath), File.GetLastWriteTimeUtc(ChainPath));
                    _loadError = null;
                    _logger.LogInformation("HTTPS certificate loaded: {Subject}, {Fingerprint}, valid until {NotAfter:u}.",
                        _current.Info.Subject, _current.Info.Sha256, _current.Info.NotAfter);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or HttpsCertificateException)
                {
                    _current = null;
                    _loadError = SafeError.Describe(ex);
                    _logger.LogWarning("The HTTPS certificate in {Directory} could not be loaded: {Error}", _directory, _loadError);
                }

                return _current;
            }
        }
    }

    /// <summary>
    /// Validates a PEM chain (leaf first) and its PEM private key, then replaces the installed pair.
    /// Nothing changes when the pair is refused.
    /// </summary>
    public HttpsCertificateInfo Import(string chainPem, string keyPem)
    {
        if (string.IsNullOrWhiteSpace(chainPem)) throw new HttpsCertificateException("The certificate chain is empty.");
        if (string.IsNullOrWhiteSpace(keyPem)) throw new HttpsCertificateException("The private key is empty.");
        var loaded = Load(chainPem, keyPem, _clock.UtcNow.UtcDateTime);
        if (loaded.Info.NotAfter <= _clock.UtcNow) throw new HttpsCertificateException($"The certificate expired on {loaded.Info.NotAfter:u}.");
        return Write(NormalizeChain(chainPem), keyPem.Trim() + "\n");
    }

    /// <summary>Validates a PKCS#12 (.pfx) file with its chain and key, then replaces the installed pair.</summary>
    public HttpsCertificateInfo ImportPfx(byte[] pfx, string? password)
    {
        X509Certificate2Collection all;
        try
        {
            all = X509CertificateLoader.LoadPkcs12Collection(pfx, password, X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException)
        {
            throw new HttpsCertificateException("The PFX could not be read; check the file and its password.");
        }

        var leaf = all.Cast<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey)
            ?? throw new HttpsCertificateException("The PFX holds no certificate with a private key.");
        var chain = new List<X509Certificate2> { leaf };
        chain.AddRange(OrderChain(leaf, all.Cast<X509Certificate2>().Where(c => !c.HasPrivateKey)));
        var key = (AsymmetricAlgorithm?)leaf.GetRSAPrivateKey() ?? (AsymmetricAlgorithm?)leaf.GetECDsaPrivateKey()
            ?? throw new HttpsCertificateException("Only RSA and ECDSA keys are supported.");
        var keyPem = ExportKeyPem(key);
        var chainPem = string.Join("\n", chain.Select(c => c.ExportCertificatePem()));
        return Import(chainPem, keyPem);
    }

    /// <summary>
    /// PKCS#8 PEM of a PFX key. Windows CNG refuses a plaintext export even of an exportable key, so the
    /// fallback goes through an encrypted export into a software key.
    /// </summary>
    private static string ExportKeyPem(AsymmetricAlgorithm key)
    {
        try
        {
            return key.ExportPkcs8PrivateKeyPem();
        }
        catch (CryptographicException)
        {
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var parameters = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000);
            var encrypted = key.ExportEncryptedPkcs8PrivateKey(password, parameters);
            using AsymmetricAlgorithm software = key is RSA ? RSA.Create() : ECDsa.Create();
            software.ImportEncryptedPkcs8PrivateKey(password, encrypted, out _);
            return software.ExportPkcs8PrivateKeyPem();
        }
    }

    /// <summary>Removes the installed certificate. HTTPS forwards stop answering until a new one is set.</summary>
    public bool Remove()
    {
        lock (_gate)
        {
            var removed = false;
            foreach (var path in new[] { ChainPath, KeyPath })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed = true;
                }
            }

            _checkedAt = DateTimeOffset.MinValue;
            return removed;
        }
    }

    private string ChainPath => Path.Combine(_directory, ChainFile);

    private string KeyPath => Path.Combine(_directory, KeyFile);

    private (DateTime, DateTime) Stamp() =>
        File.Exists(ChainPath) && File.Exists(KeyPath)
            ? (File.GetLastWriteTimeUtc(ChainPath), File.GetLastWriteTimeUtc(KeyPath))
            : default;

    private HttpsCertificateInfo Write(string chainPem, string keyPem)
    {
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(_directory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            // The key first: a reader that sees the new chain with the old key fails the load and keeps
            // trying, while the reverse order could never be told apart from a consistent pair.
            WriteAtomically(KeyPath, keyPem);
            WriteAtomically(ChainPath, chainPem);
            _checkedAt = DateTimeOffset.MinValue;
        }

        return Current?.Info ?? throw new HttpsCertificateException(LoadError ?? "The certificate was written but could not be loaded.");
    }

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp";
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(temp, content);
        }
        else
        {
            using var stream = new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            using var writer = new StreamWriter(stream);
            writer.Write(content);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static string NormalizeChain(string chainPem) =>
        string.Join("\n", PemCertificates(chainPem).Select(c => c.ExportCertificatePem())) + "\n";

    private static IEnumerable<X509Certificate2> PemCertificates(string pem)
    {
        var collection = new X509Certificate2Collection();
        try
        {
            collection.ImportFromPem(pem);
        }
        catch (CryptographicException)
        {
            throw new HttpsCertificateException("The certificate chain is not valid PEM.");
        }

        return collection.Cast<X509Certificate2>();
    }

    /// <summary>Intermediates in issuing order after the leaf, so the handshake sends a usable chain.</summary>
    private static IEnumerable<X509Certificate2> OrderChain(X509Certificate2 leaf, IEnumerable<X509Certificate2> others)
    {
        var pool = others.ToList();
        var current = leaf;
        while (pool.FirstOrDefault(c => c.SubjectName.RawData.AsSpan().SequenceEqual(current.IssuerName.RawData)) is { } issuer
               && !ReferenceEquals(issuer, current))
        {
            pool.Remove(issuer);
            yield return issuer;
            current = issuer;
        }
    }

    private static HttpsCertificate Load(string chainPem, string keyPem, DateTime installedAt)
    {
        var certificates = PemCertificates(chainPem).ToList();
        if (certificates.Count == 0) throw new HttpsCertificateException("The certificate chain holds no certificate.");

        // The leaf is the certificate the key belongs to, wherever the chain file put it.
        X509Certificate2? leaf = null;
        X509Certificate2? matched = null;
        foreach (var candidate in certificates)
        {
            try
            {
                leaf = X509Certificate2.CreateFromPem(candidate.ExportCertificatePem(), keyPem);
                matched = candidate;
                break;
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                // Not this one (or not a key at all).
            }
        }

        if (leaf is null || matched is null)
            throw new HttpsCertificateException("The private key is not valid PEM or does not belong to any certificate of the chain.");

        // SChannel cannot use the ephemeral key CreateFromPem produces; a PKCS#12 round trip gives it a real one.
        if (OperatingSystem.IsWindows())
        {
            leaf = X509CertificateLoader.LoadPkcs12(leaf.Export(X509ContentType.Pkcs12), null);
        }

        var intermediates = new X509Certificate2Collection();
        foreach (var issuer in OrderChain(leaf, certificates.Where(c => !ReferenceEquals(c, matched)))) intermediates.Add(issuer);
        var names = new List<string>();
        foreach (var extension in leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>())
        {
            names.AddRange(extension.EnumerateDnsNames());
        }

        var info = new HttpsCertificateInfo(
            leaf.Subject,
            leaf.Issuer,
            names,
            new DateTimeOffset(leaf.NotBefore.ToUniversalTime()),
            new DateTimeOffset(leaf.NotAfter.ToUniversalTime()),
            Convert.ToHexString(SHA256.HashData(leaf.RawData)),
            1 + intermediates.Count,
            new DateTimeOffset(DateTime.SpecifyKind(installedAt, DateTimeKind.Utc)));
        var context = SslStreamCertificateContext.Create(leaf, intermediates, offline: true);
        return new HttpsCertificate(context, leaf, info);
    }
}
