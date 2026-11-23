using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Construct.Agent.Tests.Support;

/// <summary>
/// The real engine (constructd's <c>Program</c> in fake mode, in-memory stores) listening on a loopback
/// HTTPS port with its own self-signed certificate, so the agent talks to it exactly as to a host:
/// over TLS, pinned, through its built-in store.
/// </summary>
public sealed class EngineHost : WebApplicationFactory<Program>
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "agent-test-engine-" + Guid.NewGuid().ToString("n"));
    private readonly int _port = FreePort();
    private readonly Action<IServiceCollection>? _services;

    /// <param name="services">Replaces engine services after composition (an SSH runner that records, a fake guest for adoption).</param>
    public EngineHost(Action<IServiceCollection>? services = null)
    {
        _services = services;
        Directory.CreateDirectory(_directory);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=engine.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        CertificatePath = Path.Combine(_directory, "engine.pfx");
        File.WriteAllBytes(CertificatePath, certificate.Export(X509ContentType.Pfx, "test"));
        Pin = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        UseKestrel();
        StartServer();
    }

    public string CertificatePath { get; }

    /// <summary>SHA-256 of the engine's certificate, as the agent pins it.</summary>
    public string Pin { get; }

    public string Url => $"https://127.0.0.1:{_port}";

    public int Port => _port;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        var settings = new Dictionary<string, string?>
        {
            ["Constructd:Fake"] = "true",
            ["Constructd:Persistence"] = "Memory",
            ["Constructd:ListenUrl"] = $"https://127.0.0.1:{_port}",
            ["Constructd:CertPath"] = Path.Combine(_directory, "engine.pfx"),
            ["Constructd:CertPassword"] = "test",
            ["Constructd:PublicHost"] = "engine.test",
            ["Constructd:Https:CertificateDirectory"] = Path.Combine(_directory, "https"),
            ["Constructd:VmReachableTimeoutMinutes"] = "1",
            ["Constructd:SshForwardPorts:Start"] = "2201",
            ["Constructd:SshForwardPorts:End"] = "2299",
            ["Constructd:AppForwardPorts:Start"] = "2300",
            ["Constructd:AppForwardPorts:End"] = "2999",
            ["Constructd:Idle:SchedulerEnabled"] = "false",
            ["Constructd:Lease:SchedulerEnabled"] = "false",
            ["Constructd:Vault:SchedulerEnabled"] = "false",
            ["Constructd:Power:KeepHostAwake"] = "false",
            ["Constructd:ForwardReconcileSeconds"] = "0",
        };
        foreach (var (key, value) in settings) builder.UseSetting(key, value);
        if (_services is not null) builder.ConfigureTestServices(_services);
    }

    public const string Commit = "c0ffee0000000000000000000000000000000001";

    /// <summary>The release this engine runs, and its source in the fake release source, so a provision job has something to copy.</summary>
    public EngineHost WithSource()
    {
        ((Constructd.Fakes.FakeReleaseInfo)Services.GetRequiredService<IReleaseInfo>()).Installed = new InstalledRelease(Commit, "1.0.0-test", null, "test");
        using var bytes = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("repo-main/bin/provision.sh", System.IO.Compression.CompressionLevel.NoCompression);
            entry.ExternalAttributes = 0x81ed << 16;
            using var s = entry.Open();
            s.Write("#!/bin/bash\n"u8);
        }

        var uri = new Uri("https://github.com/test/repo/" + Commit);
        var releases = Services.GetRequiredService<Constructd.Fakes.FakeReleaseSource>();
        releases.SourceAssets[Commit] = new(Commit, "host-" + Commit, uri, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())));
        releases.Assets[uri] = bytes.ToArray();
        return this;
    }

    /// <summary>Adds a user and returns an API token for them: the sign-in the first device uses.</summary>
    public async Task<string> AddUserAsync(string name, Role role = Role.User)
    {
        var users = Services.GetRequiredService<IUserStore>();
        var clock = Services.GetRequiredService<IClock>();
        Assert.True(await users.CreateAsync(new User(name, role, 5, clock.UtcNow, true), CancellationToken.None));
        return (await Services.GetRequiredService<ITokenService>().IssueAsync(name, "test", CancellationToken.None)).Plaintext;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_directory, true); }
        catch (IOException) { }
    }

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
