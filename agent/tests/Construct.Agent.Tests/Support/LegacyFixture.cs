using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Construct.Agent.Protection;
using Construct.Agent.Upgrade;
using Constructd.Core.Abstractions;

namespace Construct.Agent.Tests.Support;

/// <summary>DPAPI's stand-in: a fixed prefix, so a file "protected" here opens only through this fake.</summary>
public sealed class FakeDpapi : IDataProtection
{
    private static readonly byte[] Prefix = "FAKE-DPAPI:"u8.ToArray();

    public string Name => "fake-dpapi";

    public bool Weak => false;

    public byte[] Protect(ReadOnlySpan<byte> plaintext) => [.. Prefix, .. plaintext];

    public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes) =>
        protectedBytes.StartsWith(Prefix) ? protectedBytes[Prefix.Length..].ToArray() : throw new CryptographicException("Not protected for this user.");
}

/// <summary>The upgrade's OS parts over a fixture: its directories, the fake DPAPI, the old writers a test sets, SSH calls recorded.</summary>
public sealed class FakeUpgradePlatform(LegacyFixture fixture) : IUpgradePlatform
{
    public List<string> Writers { get; } = [];

    public List<(string Instance, string Command)> VmCalls { get; } = [];

    public Func<LegacyVmTarget, int> VmExit { get; set; } = _ => 0;

    public IDataProtection? LegacyProtection { get; } = fixture.Protection;

    public string? LocalAppData => fixture.LocalAppData;

    public string Home => fixture.Home;

    public IReadOnlyList<string> OldWriters() => Writers.ToArray();

    /// <summary>The elevated run; by default none happens. Tests of the engine setup act as the installer here.</summary>
    public Func<string, IReadOnlyList<string>, int>? Elevated { get; set; }

    public List<IReadOnlyList<string>> ElevatedRuns { get; } = [];

    public bool CanHostEngine { get; set; } = true;

    public int Restarts { get; private set; }

    public Task<int> RunElevatedAsync(string program, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        if (Elevated is null) throw new InvalidOperationException("Tests install no engine elevated.");
        ElevatedRuns.Add(arguments);
        return Task.FromResult(Elevated(program, arguments));
    }

    public Task RestartWindowsAsync(CancellationToken ct)
    {
        Restarts++;
        return Task.CompletedTask;
    }

    public Task<VmShellResult> RunOnVmAsync(LegacyVmTarget target, IReadOnlyList<string> command, byte[]? stdin, CancellationToken ct)
    {
        lock (VmCalls) VmCalls.Add((target.Instance, string.Join(' ', command)));
        Assert.True(File.Exists(target.KeyFile), "The old client's key for " + target.Instance);
        if (command.Contains("add")) Assert.Contains("BEGIN PUBLIC KEY", Encoding.ASCII.GetString(stdin!));
        return Task.FromResult(new VmShellResult(VmExit(target), ""));
    }
}

/// <summary>A guest that answers the adoption's identity read with the firmware id the fake Hyper-V reports.</summary>
public sealed class AdoptableGuest : ISshRunner
{
    public const string Firmware = "0b9a2d11-1c2b-4f7e-9e5a-2f63a0c1d2e3";

    public List<string> Commands { get; } = [];

    /// <summary>The fingerprints enrolled as backup recipients on the guest.</summary>
    public HashSet<string> Recipients { get; } = [];

    /// <summary>How many switch-overs fail (exit 1) before one succeeds.</summary>
    public int FailSwitches { get; set; }

    public Task<int> RunAsync(SshTarget target, string command, string? standardInput, IProgress<string>? lines, TimeSpan timeout, CancellationToken ct)
    {
        lock (Commands) Commands.Add(command);
        // The guest's backup recipients (construct-backup.sh), by fingerprint.
        if (command.Contains("recipients add", StringComparison.Ordinal) && standardInput is not null)
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(standardInput);
            lock (Recipients) Recipients.Add(Convert.ToHexStringLower(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())));
            return Task.FromResult(0);
        }

        if (command.Contains("recipients list", StringComparison.Ordinal))
        {
            lock (Recipients) lines?.Report(new JsonArray(Recipients.Select(fp => (JsonNode)new JsonObject { ["fingerprint"] = fp, ["label"] = "PC" }).ToArray()).ToJsonString());
            return Task.FromResult(0);
        }

        if (command.Contains("recipients remove ", StringComparison.Ordinal))
        {
            lock (Recipients) Recipients.RemoveWhere(fp => command.Contains("recipients remove " + fp, StringComparison.Ordinal));
            return Task.FromResult(0);
        }

        if (command.StartsWith("cat /etc/machine-id", StringComparison.Ordinal))
        {
            lines?.Report("0123456789abcdef0123456789abcdef");
            lines?.Report(Firmware);
            return Task.FromResult(0);
        }

        if (FailSwitches > 0)
        {
            FailSwitches--;
            return Task.FromResult(1);
        }

        return Task.FromResult(0);
    }

    public Task CopyAsync(SshTarget target, string localPath, string remotePath, CancellationToken ct) => throw new InvalidOperationException();
}

/// <summary>
/// An old client's install on disk, written in its formats: <c>%LOCALAPPDATA%\The-Construct\</c> with the install
/// folder, <c>instances.json</c>, <c>remote\</c>, the config repository (git), <c>backup\</c>, and the vault in
/// <c>The-Construct-Vault\vault.dat</c>, all protected through <see cref="FakeDpapi"/>; <c>~\.ssh</c> in a home.
/// </summary>
public sealed class LegacyFixture : IDisposable
{
    public const string PrivateKeyText = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAAfixture\n-----END OPENSSH PRIVATE KEY-----\n";

    public LegacyFixture()
    {
        Directory.CreateDirectory(InstallFolder);
        Directory.CreateDirectory(Path.Combine(Home, ".ssh"));
    }

    public string Directory_ { get; } = Path.Combine(Path.GetTempPath(), "legacy-fixture-" + Guid.NewGuid().ToString("n"));

    public string LocalAppData => Path.Combine(Directory_, "local");

    public string Home => Path.Combine(Directory_, "home");

    public string Root => Path.Combine(LocalAppData, "The-Construct");

    public string InstallFolder => Path.Combine(Root, "permissionBRICK-The-Construct-main", "The-Construct-main");

    public FakeDpapi Protection { get; } = new();

    public byte[] VaultKey { get; } = RandomNumberGenerator.GetBytes(32);

    public void Write(string relative, string text, string? root = null)
    {
        var path = Path.Combine(root ?? LocalAppData, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Settings(JsonObject settings) => Write(Path.Combine(InstallFolder, ".construct-settings.json"), settings.ToJsonString());

    public void SshKey(string keyName) => Write(Path.Combine(".ssh", keyName), PrivateKeyText, Home);

    public void Instances(JsonObject registry) => Write(Path.Combine("The-Construct", "instances.json"), registry.ToJsonString());

    /// <summary>A host the old client enrolled: <c>remote\&lt;slug&gt;.pin</c> as colon pairs, the token DPAPI-protected and base64.</summary>
    public void RemoteHost(string url, string pin, string token)
    {
        var slug = LegacyState.Slug(url);
        Write(Path.Combine("The-Construct", "remote", slug + ".pin"), string.Join(':', Enumerable.Range(0, 32).Select(i => pin.Substring(i * 2, 2))));
        Write(Path.Combine("The-Construct", "remote", slug + ".token"), Convert.ToBase64String(Protection.Protect(Encoding.UTF8.GetBytes(token))));
    }

    /// <summary>vault.dat as the Companion writes it (version 2): base64 of the protected JSON.</summary>
    public void Vault(IEnumerable<(string Name, string Value, string? Username)> secrets, JsonObject? hosts = null, int leases = 0)
    {
        var document = new JsonObject
        {
            ["v"] = 2, ["installId"] = "pc-1", ["key"] = Convert.ToBase64String(VaultKey),
            ["secrets"] = new JsonArray(secrets.Select(s => (JsonNode)new JsonObject
            {
                ["name"] = s.Name, ["description"] = "", ["username"] = s.Username ?? "", ["secret"] = s.Value, ["createdAt"] = 1, ["updatedAt"] = 2,
                ["origin"] = "user", ["updatedBy"] = "pc:pc-1", ["autoApprove"] = s.Name.StartsWith("auto", StringComparison.Ordinal),
            }).ToArray()),
            ["leases"] = new JsonArray(Enumerable.Range(0, leases).Select(i => (JsonNode)new JsonObject { ["id"] = "l" + i, ["instance"] = "agent-vm", ["name"] = "github" }).ToArray()),
            ["cleanups"] = new JsonArray(), ["tombstones"] = new JsonArray(), ["hosts"] = hosts ?? [],
        };
        Write(Path.Combine("The-Construct-Vault", "vault.dat"), Convert.ToBase64String(Protection.Protect(Encoding.UTF8.GetBytes(document.ToJsonString()))));
    }

    /// <summary>backup\pc-key.dpapi: the protected XML of an RSA-3072 key, as lib/AgentVm.Backup.ps1 writes it; its fingerprint.</summary>
    public string BackupKey()
    {
        using var rsa = RSA.Create(3072);
        var path = Path.Combine(Root, "backup", "pc-key.dpapi");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Protection.Protect(Encoding.UTF8.GetBytes(rsa.ToXmlString(true))));
        BackupPublicPem = rsa.ExportSubjectPublicKeyInfoPem();
        return BackupFingerprint = Convert.ToHexStringLower(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
    }

    public string? BackupFingerprint { get; private set; }

    public string? BackupPublicPem { get; private set; }

    /// <summary>The config repository: <c>main</c> with the profiles, then a VM branch with an unmerged edit.</summary>
    public void ConfigRepo(IReadOnlyDictionary<string, string> main, string? branch = null, IReadOnlyDictionary<string, string?>? branchFiles = null,
        IReadOnlyDictionary<string, string>? mainAfter = null)
    {
        var repo = Path.Combine(Root, "config");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q", "-b", "main");
        foreach (var (file, text) in main) Write(Path.Combine(repo, file), text);
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "main");
        if (branch is null) return;
        Git(repo, "checkout", "-q", "-b", branch);
        foreach (var (file, text) in branchFiles!)
        {
            if (text is null) File.Delete(Path.Combine(repo, file));
            else Write(Path.Combine(repo, file), text);
        }

        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "vm edit");
        Git(repo, "checkout", "-q", "main");
        if (mainAfter is null) return;
        foreach (var (file, text) in mainAfter) Write(Path.Combine(repo, file), text);
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "main edit");
    }

    /// <summary>A further branch off main with its own edits (a second VM).</summary>
    public void Branch(string branch, IReadOnlyDictionary<string, string> files)
    {
        var repo = Path.Combine(Root, "config");
        Git(repo, "checkout", "-q", "-b", branch, "main");
        foreach (var (file, text) in files) Write(Path.Combine(repo, file), text);
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", branch + " edit");
        Git(repo, "checkout", "-q", "main");
    }

    private static void Git(string repo, params string[] args)
    {
        var info = new ProcessStartInfo("git") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-C", repo, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "-c", "commit.gpgsign=false" }) info.ArgumentList.Add(a);
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, "git " + string.Join(' ', args) + ": " + error);
    }

    /// <summary>SHA-256 of every file under the fixture (the old state and ~/.ssh), by relative path.</summary>
    public SortedDictionary<string, string> Digests()
    {
        var digests = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Directory_, "*", SearchOption.AllDirectories))
            digests[Path.GetRelativePath(Directory_, file)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        return digests;
    }

    public void Dispose()
    {
        try { Directory.Delete(Directory_, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
