using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// engine-host [--dir DIR]: starts the engine and prints one JSON line {url, pin, token}; runs until stdin
/// closes. Not top-level statements: those would be a <c>Program</c> that hides the engine's.
/// </summary>
static class EngineHostMain
{
    public static async Task<int> Main(string[] args)
    {
        var dir = args.Length == 2 && args[0] == "--dir" ? args[1] : Path.Combine(Path.GetTempPath(), "ui-e2e-engine-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        // WebApplicationFactory otherwise looks for a solution above the executable to find the content root.
        Environment.SetEnvironmentVariable("ASPNETCORE_TEST_CONTENTROOT_CONSTRUCTD_API", dir);
        await using var engine = new E2eEngine(dir);
        engine.Start();
        Console.WriteLine(JsonSerializer.Serialize(new { url = engine.Url, pin = engine.Pin, token = E2eEngine.AdminToken }));
        Console.Out.Flush();
        await Console.In.ReadToEndAsync();
        return 0;
    }
}

sealed class E2eEngine(string directory) : WebApplicationFactory<Program>
{
    public const string AdminToken = "e2e-admin-token-0123456789abcdef";
    public const string Commit = "c0ffee0000000000000000000000000000000001";

    private readonly int _port = FreePort();
    private string _pfx = "";

    public string Url => $"https://127.0.0.1:{_port}";
    public string Pin { get; private set; } = "";

    public void Start()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=engine.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        _pfx = Path.Combine(directory, "engine.pfx");
        File.WriteAllBytes(_pfx, certificate.Export(X509ContentType.Pfx, "test"));
        Pin = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        UseKestrel();
        StartServer();
        // The release this engine runs, and its source in the cache, so a provision job has something to copy.
        ((FakeReleaseInfo)Services.GetRequiredService<IReleaseInfo>()).Installed = new InstalledRelease(Commit, "1.0.0-e2e", null, "e2e");
        var zip = SourceZip();
        var uri = new Uri("https://github.com/test/repo/" + Commit);
        var releases = Services.GetRequiredService<FakeReleaseSource>();
        releases.SourceAssets[Commit] = new(Commit, "host-" + Commit, uri, zip.Length, Convert.ToHexStringLower(SHA256.HashData(zip)));
        releases.Assets[uri] = zip;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        var settings = new Dictionary<string, string?>
        {
            ["Constructd:Fake"] = "true",
            // SQLite, not memory: the capacity ledger reconciles only there, and VM creation needs it.
            ["Constructd:Persistence"] = "Sqlite",
            ["Constructd:DatabasePath"] = Path.Combine(directory, "constructd.db"),
            ["Constructd:ListenUrl"] = Url,
            ["Constructd:CertPath"] = Path.Combine(directory, "engine.pfx"),
            ["Constructd:CertPassword"] = "test",
            ["Constructd:PublicHost"] = "engine.test",
            ["Constructd:Https:CertificateDirectory"] = Path.Combine(directory, "https"),
            ["Constructd:BootstrapAdmin"] = "admin",
            ["Constructd:BootstrapAdminToken"] = AdminToken,
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
        builder.ConfigureTestServices(services => services.AddSingleton<ISshRunner, ScriptedGuest>());
    }

    private static byte[] SourceZip()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            foreach (var (name, mode, text) in new[] { ("repo-main/bin/provision.sh", 0x81ed, "#!/bin/bash\n"), ("repo-main/README.md", 0x81a4, "readme\n") })
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                entry.ExternalAttributes = mode << 16;
                using var s = entry.Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        return bytes.ToArray();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(directory, true); }
        catch (IOException) { }
    }
}

/// <summary>
/// A guest that answers each command of the provision job as a clean run would, a little slowly so the UI
/// shows the job running, and keeps the provisioned.env that provision.sh would write.
/// </summary>
sealed partial class ScriptedGuest : ISshRunner
{
    private readonly Dictionary<string, string> _marker = new(StringComparer.Ordinal);

    public async Task<int> RunAsync(SshTarget target, string command, string? standardInput, IProgress<string>? lines, TimeSpan timeout, CancellationToken ct)
    {
        await Task.Delay(400, ct);
        string[] output = [];
        if (command.Contains("bin/provision.sh", StringComparison.Ordinal) && !command.Contains("PROVISION_PHASE", StringComparison.Ordinal))
        {
            lock (_marker)
            {
                foreach (var (name, key) in new[] { ("CONSTRUCT_VERSION", "CONSTRUCT_COMMIT"), ("CONSTRUCT_PAYLOAD_HASH", "CONSTRUCT_PAYLOAD_HASH"),
                    ("CONSTRUCT_DESIRED_HASH", "CONSTRUCT_DESIRED_HASH"), ("CONSTRUCT_CHANNEL", "CONSTRUCT_CHANNEL") })
                    if (Variable(command, name) is { } value) _marker[key] = value;
                _marker["REPROVISIONED_AT"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            }
            output = ["installing...", "===CONSTRUCT-PROVISION-RESULT===", "errors=0", "===END-CONSTRUCT-PROVISION-RESULT==="];
        }
        else if (command.Contains("PROVISION_PHASE", StringComparison.Ordinal))
            output = ["===CONSTRUCT-PROVISION-RESULT===", "errors=0", "===END-CONSTRUCT-PROVISION-RESULT==="];
        else if (command.Contains("provisioned.env", StringComparison.Ordinal))
            lock (_marker) output = _marker.Select(kv => $"{kv.Key}={kv.Value}").ToArray();
        else if (command.Contains("reboot-required", StringComparison.Ordinal))
            output = ["no"];
        foreach (var line in output) lines?.Report(line);
        return 0;
    }

    public Task CopyAsync(SshTarget target, string localPath, string remotePath, CancellationToken ct) => Task.Delay(200, ct);

    [GeneratedRegex(@"(?<![A-Z_])([A-Z0-9_]+)='((?:[^']|'\\'')*)'")]
    private static partial Regex Assignment();

    private static string? Variable(string command, string name)
    {
        var start = command.IndexOf("bash -lc '", StringComparison.Ordinal);
        var inner = start < 0 ? command : command[(start + 10)..command.LastIndexOf("' 2>&1", StringComparison.Ordinal)].Replace("'\\''", "'", StringComparison.Ordinal);
        foreach (Match m in Assignment().Matches(inner))
            if (m.Groups[1].Value == name) return m.Groups[2].Value.Replace("'\\''", "'", StringComparison.Ordinal);
        return null;
    }
}
