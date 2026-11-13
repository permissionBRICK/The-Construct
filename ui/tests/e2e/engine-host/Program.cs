using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Constructd.Core.Logic;
using System.Text.RegularExpressions;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// engine-host [--dir DIR]: starts the engine and prints one JSON line {url, pin, token, dir}; runs until stdin
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
        Console.WriteLine(JsonSerializer.Serialize(new { url = engine.Url, pin = engine.Pin, token = E2eEngine.AdminToken, dir }));
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
            ["Constructd:Backups:RootDir"] = Path.Combine(directory, "backups"),
        };
        foreach (var (key, value) in settings) builder.UseSetting(key, value);
        builder.ConfigureTestServices(services => services.AddSingleton<ISshRunner>(sp => new ScriptedGuest(Url, directory, sp)));
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
/// shows the job running, and keeps the provisioned.env that provision.sh would write. It also answers
/// <c>construct backup</c> as the service tests' FakeBackupGuest does, so a reinstall runs to its end: recipients
/// are enrolled and removed, <c>create</c> uploads a backup through the engine's routes with the VM token the
/// test leaves in <c>vm-token</c> beside the database (a token change waits on the reinstall's own VM lock),
/// wrapped for every recipient; <c>verify</c> and <c>restore</c> check the key on stdin. It starts with one
/// recipient standing for the user's device; once the test names the account in <c>account</c>, the account's
/// backup key <c>B</c> from the store is a recipient too, so a device can open a stored backup. The provision
/// script fails once with a critical exit when the test leaves <c>fail-next</c> beside the database.
/// </summary>
sealed partial class ScriptedGuest(string engineUrl, string directory, IServiceProvider services) : ISshRunner
{
    private readonly Dictionary<string, string> _marker = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Pem, string Label)> _recipients = Device();
    private readonly Dictionary<string, (string Key, Dictionary<string, (string Pem, string Label)> Recipients)> _made = new(StringComparer.Ordinal);

    private static Dictionary<string, (string Pem, string Label)> Device()
    {
        var device = BackupJobKey.Create();
        return new(StringComparer.Ordinal) { [device.Fingerprint] = (device.PublicPem, "PC") };
    }

    public async Task<int> RunAsync(SshTarget target, string command, string? standardInput, IProgress<string>? lines, TimeSpan timeout, CancellationToken ct)
    {
        await Task.Delay(400, ct);
        var inner = Unwrap(command);
        if (inner.Contains("construct-backup.sh", StringComparison.Ordinal))
            return await BackupAsync(inner, standardInput, lines, ct);
        string[] output = [];
        if (command.Contains("bin/provision.sh", StringComparison.Ordinal) && !command.Contains("PROVISION_PHASE", StringComparison.Ordinal)
            && File.Exists(Path.Combine(directory, "fail-next")))
        {
            File.Delete(Path.Combine(directory, "fail-next"));
            foreach (var line in new[] { "apt-get: could not get lock", "===CONSTRUCT-PROVISION-RESULT===", "errors=0", "===END-CONSTRUCT-PROVISION-RESULT===" }) lines?.Report(line);
            return 2;
        }
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

    private static string Unwrap(string command)
    {
        var start = command.IndexOf("bash -lc '", StringComparison.Ordinal);
        if (start < 0) return command;
        var end = command.LastIndexOf("' 2>&1", StringComparison.Ordinal);
        return command[(start + 10)..end].Replace("'\\''", "'", StringComparison.Ordinal);
    }

    private async Task<int> BackupAsync(string inner, string? standardInput, IProgress<string>? lines, CancellationToken ct)
    {
        var args = inner[(inner.IndexOf("construct-backup.sh", StringComparison.Ordinal) + "construct-backup.sh".Length)..].Trim().Split(' ');
        var key = (standardInput ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        lock (_recipients)
        {
            switch (args[0], args.Length > 1 ? args[1] : "")
            {
                case ("recipients", "add"):
                {
                    var pem = standardInput![standardInput.IndexOf("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal)..];
                    var fp = BackupJobKey.PublicFingerprint(pem)!;
                    _recipients[fp] = (pem, inner[(inner.IndexOf("--label ", StringComparison.Ordinal) + 8)..].Trim().Trim('\''));
                    lines?.Report(fp);
                    return 0;
                }
                case ("recipients", "list"):
                    lines?.Report(new JsonArray(_recipients.Select(r => (JsonNode)new JsonObject { ["fingerprint"] = r.Key, ["label"] = r.Value.Label, ["addedAt"] = null }).ToArray()).ToJsonString());
                    return 0;
                case ("recipients", "remove"):
                    _recipients.Remove(args[2]);
                    return 0;
                case ("verify", _):
                    // verify --from-host ID, restore --from-host ID
                    return _made.TryGetValue(args[^1], out var made) && made.Key == key ? 0 : 8;
                case ("restore", _):
                    if (!_made.TryGetValue(args[^1], out var backup) || backup.Key != key) return 8;
                    foreach (var (fp, r) in backup.Recipients) _recipients.TryAdd(fp, r);
                    lines?.Report($"construct backup: backup {args[^1]} restored");
                    return 0;
                case ("create", _):
                    break;
                default:
                    return 2;
            }
        }

        return await CreateAsync(lines, ct);
    }

    private async Task<int> CreateAsync(IProgress<string>? lines, CancellationToken ct)
    {
        var tokenFile = Path.Combine(directory, "vm-token");
        if (!File.Exists(tokenFile)) return 6;
        var parts = File.ReadAllText(tokenFile).Trim().Split(' ');
        var (vm, token) = (parts[0], parts[1]);
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { BaseAddress = new Uri(engineUrl) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("VmToken", token);
        var e = "AGE-SECRET-KEY-1" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        using var opened = await http.PostAsJsonAsync($"/api/v1/vms/{vm}/backups", new { trigger = "pre-reinstall", estimatedBytes = 100 }, ct);
        if (opened.StatusCode != HttpStatusCode.Created) return 6;
        var id = (await opened.Content.ReadFromJsonAsync<JsonObject>(ct))!["id"]!.GetValue<string>();
        Dictionary<string, (string Pem, string Label)> recipients;
        lock (_recipients) recipients = new(_recipients, StringComparer.Ordinal);
        if (AccountBackupKey() is { } account) recipients[BackupJobKey.PublicFingerprint(account)!] = (account, "account");
        var keys = new JsonObject
        {
            ["format"] = "construct-backup-keys/1", ["backupId"] = id, ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["recipients"] = new JsonArray(recipients.Select(r => (JsonNode)new JsonObject
            {
                ["fingerprint"] = r.Key, ["label"] = r.Value.Label, ["wrapped"] = BackupJobKey.Wrap(e, r.Value.Pem),
            }).ToArray()),
        };
        using var put = await http.PutAsync($"/api/v1/vms/{vm}/backups/{id}/keys", new StringContent(keys.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        if (put.StatusCode != HttpStatusCode.NoContent) return 7;
        var content = new ByteArrayContent(RandomNumberGenerator.GetBytes(256));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var upload = await http.PutAsync($"/api/v1/vms/{vm}/backups/{id}/content", content, ct);
        if (upload.StatusCode != HttpStatusCode.OK) return 7;
        lock (_recipients) _made[id] = (e, recipients);
        lines?.Report($"{{\"id\":\"{id}\",\"sizeBytes\":256,\"sha256\":\"x\",\"trigger\":\"pre-reinstall\",\"gitCredentialHosts\":[]}}");
        return 0;
    }

    /// <summary>The public half of the account's <c>B</c> as the store keeps it, once the test named the account.</summary>
    private string? AccountBackupKey()
    {
        var file = Path.Combine(directory, "account");
        if (!File.Exists(file)) return null;
        var keys = services.GetRequiredService<Constructd.Store.Identity.StoreDirectory>().Keys(File.ReadAllText(file).Trim());
        if (keys is null) return null;
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(keys.BackupPublic, out _);
        return rsa.ExportSubjectPublicKeyInfoPem();
    }

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
