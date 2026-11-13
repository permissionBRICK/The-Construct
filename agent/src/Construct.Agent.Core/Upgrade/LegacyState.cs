using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Agent.Protection;

namespace Construct.Agent.Upgrade;

/// <summary>One VM of the old client's registry (<c>instances.json</c>, or the implicit default).</summary>
/// <param name="Settings">For a local VM: the install's <c>.construct-settings.json</c> (git identity, and the default VM's own keys).</param>
/// <param name="InstanceState">For a named local VM: its <c>instances\&lt;name&gt;.json</c>; null for the default VM.</param>
public sealed record LegacyInstance(string Name, bool Local, string SshHost, int SshPort, string KeyName, string ConfigBranch,
    string? ServiceUrl, JsonObject? Settings, JsonObject? InstanceState);

/// <summary>A host service the old client knew (a remote instance's or one the Companion enrolled), with its pin; the token stays on disk until used.</summary>
public sealed record LegacyHost(string Url, string Slug, string? Pin, bool HasToken, string? VaultMode);

/// <summary>
/// Everything the old client left on this PC (13.1), read only. Paths follow the old client:
/// <c>%LOCALAPPDATA%\The-Construct\</c> (<c>instances.json</c>, <c>instances\</c>, <c>remote\</c>,
/// <c>config\</c>, <c>backup\</c>, <c>companion\</c>), <c>%LOCALAPPDATA%\The-Construct-Vault\vault.dat</c>
/// and the install folder <c>The-Construct\&lt;repo&gt;-&lt;ref&gt;\&lt;name&gt;-&lt;ref&gt;\</c> with its
/// <c>.construct-settings.json</c>. Nothing here writes, and no secret is read until a step asks for it.
/// </summary>
public sealed class LegacyState
{
    public const string DefaultInstance = "agent-vm";
    private static readonly Regex NameRule = new(@"\A[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\z", RegexOptions.CultureInvariant);
    private static readonly Regex KeyFileRule = new(@"\A[A-Za-z0-9._-]{1,128}\z", RegexOptions.CultureInvariant);
    private readonly IDataProtection? _protection;

    private LegacyState(string localAppData, string? installFolder, string home, IDataProtection? protection)
    {
        LocalAppData = localAppData;
        InstallFolder = installFolder;
        Home = home;
        _protection = protection;
    }

    public string LocalAppData { get; }

    /// <summary>The previous install folder (its <c>.construct-settings.json</c>), or null when none was found.</summary>
    public string? InstallFolder { get; }

    /// <summary>Other install folders found besides <see cref="InstallFolder"/> (an install moved between refs, 12A.5).</summary>
    public IReadOnlyList<string> OtherInstallFolders { get; private init; } = [];

    public string Home { get; }

    public string Root => Path.Combine(LocalAppData, "The-Construct");

    public string ConfigRepository => Path.Combine(Root, "config");

    public string VaultFile => Path.Combine(LocalAppData, "The-Construct-Vault", "vault.dat");

    public string BackupDirectory => Path.Combine(Root, "backup");

    public IReadOnlyList<string> Problems { get; private init; } = [];

    /// <summary>True when anything of the old client is on this PC.</summary>
    public bool Found => Directory.Exists(Root) || File.Exists(VaultFile);

    /// <summary>
    /// Opens the old state under <paramref name="localAppData"/>. <paramref name="from"/> names the previous
    /// install folder (the one holding <c>.construct-settings.json</c>, or its <c>&lt;repo&gt;-&lt;ref&gt;</c>
    /// parent); without it the most recently written one under <c>The-Construct\</c> is taken.
    /// </summary>
    public static LegacyState Open(string localAppData, string? from, string home, IDataProtection? protection)
    {
        var root = Path.Combine(localAppData, "The-Construct");
        var problems = new List<string>();
        var candidates = new List<string>();
        if (Directory.Exists(root))
            foreach (var slug in Directory.EnumerateDirectories(root))
                foreach (var folder in Directory.EnumerateDirectories(slug))
                    if (File.Exists(Path.Combine(folder, ".construct-settings.json"))) candidates.Add(folder);
        candidates = candidates.OrderByDescending(f => File.GetLastWriteTimeUtc(Path.Combine(f, ".construct-settings.json"))).ToList();

        string? install = null;
        if (from is { Length: > 0 })
        {
            var full = Path.GetFullPath(from);
            if (File.Exists(Path.Combine(full, ".construct-settings.json"))) install = full;
            else if (Directory.Exists(full))
                install = Directory.EnumerateDirectories(full).FirstOrDefault(d => File.Exists(Path.Combine(d, ".construct-settings.json")));
            if (install is null) throw new AgentFailure(400, "no-install-folder", "The folder given has no .construct-settings.json of the old client.");
        }
        else
        {
            install = candidates.FirstOrDefault();
        }

        return new LegacyState(localAppData, install, home, protection)
        {
            OtherInstallFolders = candidates.Where(c => !string.Equals(c, install, StringComparison.OrdinalIgnoreCase)).ToArray(),
            Problems = problems,
        };
    }

    // ---- the install folder ----

    /// <summary><c>.construct-settings.json</c> of the install folder, or an empty object.</summary>
    public JsonObject InstallSettings() => InstallFolder is null ? [] : ReadObject(Path.Combine(InstallFolder, ".construct-settings.json")) ?? [];

    /// <summary>The device channel the old ref means (12A.5): <c>main</c> is stable, any other ref nightly.</summary>
    public string Channel() => InstallSettings()["constructRef"] is JsonValue v && v.TryGetValue<string>(out var r) && r.Length > 0 && r != "main" ? "nightly" : "stable";

    /// <summary>The old client's bootstrap key, which the solo engine takes over (<c>keys\bootstrap_ed25519</c>).</summary>
    public string? BootstrapKey()
    {
        if (InstallFolder is null) return null;
        var key = Path.Combine(InstallFolder, "keys", "bootstrap_ed25519");
        return File.Exists(key) && File.Exists(key + ".pub") ? key : null;
    }

    // ---- the registry ----

    /// <summary>
    /// The VMs the old client knew. Without <c>instances.json</c> the one default local VM (<c>agent-vm</c>),
    /// as the old client synthesizes it; entries that break the old client's own rules are skipped with a problem.
    /// </summary>
    public IReadOnlyList<LegacyInstance> Instances()
    {
        var settings = InstallSettings();
        var path = Path.Combine(Root, "instances.json");
        var document = ReadObject(path);
        if (document?["instances"] is not JsonObject entries)
            return [new LegacyInstance(DefaultInstance, true, "agent-vm.mshome.net", 22, "agent_vm_ed25519", "vm", null, settings, null)];

        var list = new List<LegacyInstance>();
        foreach (var (name, node) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (node is not JsonObject raw || !NameRule.IsMatch(name) || name.StartsWith("construct-", StringComparison.OrdinalIgnoreCase)) continue;
            var isDefault = name == DefaultInstance;
            var backend = Text(raw, "backend")?.ToLowerInvariant() ?? "hyperv-local";
            var local = backend == "hyperv-local";
            if (!local && backend != "hyperv-remote") continue;
            var keyName = Text(raw, "keyName") ?? (isDefault ? "agent_vm_ed25519" : "construct_" + name + "_ed25519");
            if (!KeyFileRule.IsMatch(keyName)) continue;
            var host = Text(raw, "sshHost") ?? Text(raw, "vmHost") ?? (isDefault ? "agent-vm.mshome.net" : name + ".mshome.net");
            var port = raw["sshPort"] is JsonValue p && p.TryGetValue<double>(out var d) && d is >= 1 and <= 65535 ? (int)d : 22;
            var branch = Text(raw, "configBranch") ?? (isDefault ? "vm" : "vm-" + name);
            var service = raw["service"] is JsonObject s ? Text(s, "url") : null;
            if (!local && service is null) continue;
            JsonObject? state = null;
            if (local && !isDefault) state = ReadObject(Path.Combine(Root, "instances", name + ".json")) ?? new JsonObject();
            list.Add(new LegacyInstance(name, local, host, port, keyName, branch, service is null ? null : NormalizeUrl(service), local ? settings : null, state));
        }

        return list;
    }

    // ---- remote hosts ----

    /// <summary>Every host service a remote instance points at or the Companion enrolled (<c>companion\hosts.json</c>), once.</summary>
    public IReadOnlyList<LegacyHost> Hosts()
    {
        var urls = Instances().Where(i => i.ServiceUrl is not null).Select(i => i.ServiceUrl!).ToList();
        if (ReadObject(Path.Combine(Root, "companion", "hosts.json"))?["hosts"] is JsonArray enrolled)
            foreach (var h in enrolled.OfType<JsonObject>())
                if (Text(h, "url") is { } url)
                {
                    try { urls.Add(NormalizeUrl(url)); }
                    catch (UriFormatException) { }
                }

        var modes = VaultHostModes();
        return urls.Distinct(StringComparer.OrdinalIgnoreCase).Select(url =>
        {
            var slug = Slug(url);
            var pinFile = Path.Combine(Root, "remote", slug + ".pin");
            var pin = File.Exists(pinFile) ? Regex.Replace(File.ReadAllText(pinFile), "[^0-9A-Fa-f]", "").ToUpperInvariant() : null;
            return new LegacyHost(url, slug, pin is { Length: 64 } ? pin : null, File.Exists(Path.Combine(Root, "remote", slug + ".token")),
                modes.GetValueOrDefault(slug));
        }).ToArray();
    }

    /// <summary>The API token the old client stored for a host (DPAPI), or null. The caller keeps it in memory only.</summary>
    public string? HostToken(LegacyHost host)
    {
        var path = Path.Combine(Root, "remote", host.Slug + ".token");
        if (!File.Exists(path)) return null;
        var raw = Unprotect(Convert.FromBase64String(File.ReadAllText(path).Trim()), "a host's token");
        try { return Encoding.UTF8.GetString(raw); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    /// <summary>The old client's base URL form: https unless stated, the service's port 7462 by default, no path.</summary>
    public static string NormalizeUrl(string value)
    {
        var v = value.Trim();
        if (!Regex.IsMatch(v, "^[A-Za-z][A-Za-z0-9+.-]*://")) v = "https://" + v;
        var uri = new Uri(v, UriKind.Absolute);
        var port = uri.IsDefaultPort ? AgentOptions.DefaultEnginePort : uri.Port;
        return $"{uri.Scheme}://{uri.Host}:{port}".ToLowerInvariant();
    }

    /// <summary><c>Get-ConstructRemoteHostSlug</c>: host_port, lowercased, anything else <c>_</c>.</summary>
    public static string Slug(string url)
    {
        var uri = new Uri(NormalizeUrl(url));
        return Regex.Replace($"{uri.DnsSafeHost}_{uri.Port}".ToLowerInvariant(), "[^a-z0-9._-]", "_");
    }

    // ---- the vault ----

    public bool HasVault => File.Exists(VaultFile);

    /// <summary>The vault document (secrets, K, per-host modes). Throws when it exists and cannot be opened.</summary>
    public LegacyVault ReadVault()
    {
        var raw = Unprotect(Convert.FromBase64String(File.ReadAllText(VaultFile).Trim().TrimStart('﻿')), "the key vault");
        try
        {
            var root = JsonNode.Parse(raw) as JsonObject ?? throw Damaged();
            byte[]? key = null;
            if (Text(root, "key") is { } k)
            {
                key = Convert.FromBase64String(k);
                if (key.Length != 32) throw Damaged();
            }

            var secrets = new List<LegacySecret>();
            foreach (var s in (root["secrets"] as JsonArray ?? []).OfType<JsonObject>())
                if (Text(s, "name") is { } name && Text(s, "secret") is { } value)
                    secrets.Add(new LegacySecret(name, Text(s, "description") ?? "", Text(s, "username"), value, s["autoApprove"] is JsonValue a && a.TryGetValue<bool>(out var on) && on));
            return new LegacyVault(key, secrets, (root["leases"] as JsonArray)?.Count ?? 0, (root["cleanups"] as JsonArray)?.Count ?? 0);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw Damaged();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }

        static AgentFailure Damaged() => new(409, "legacy-vault-damaged", "The old key vault file is damaged.");
    }

    /// <summary>Per-host vault modes the Companion recorded (by host slug); readable without the key only when the vault opens.</summary>
    private Dictionary<string, string> VaultHostModes()
    {
        var modes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!HasVault || _protection is null) return modes;
        try
        {
            var raw = Unprotect(Convert.FromBase64String(File.ReadAllText(VaultFile).Trim().TrimStart('﻿')), "the key vault");
            try
            {
                if (JsonNode.Parse(raw)?["hosts"] is JsonObject hosts)
                    foreach (var (slug, node) in hosts)
                        if (node is JsonObject h && Text(h, "mode") is "available" or "locked") modes[slug] = Text(h, "mode")!;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(raw);
            }
        }
        catch (Exception ex) when (ex is AgentFailure or JsonException or FormatException)
        {
        }

        return modes;
    }

    // ---- the backup keys ----

    /// <summary>The PC's backup private keys: <c>pc-key.dpapi</c> and the replaced <c>pc-key.&lt;fp&gt;.dpapi</c>.</summary>
    public IReadOnlyList<string> BackupKeyFiles()
    {
        if (!Directory.Exists(BackupDirectory)) return [];
        var current = Path.Combine(BackupDirectory, "pc-key.dpapi");
        var list = new List<string>();
        if (File.Exists(current)) list.Add(current);
        list.AddRange(Directory.EnumerateFiles(BackupDirectory)
            .Where(f => Regex.IsMatch(Path.GetFileName(f), @"\Apc-key\.[0-9a-f]{64}\.dpapi\z")).OrderBy(f => f, StringComparer.Ordinal));
        return list;
    }

    /// <summary>One backup key: its fingerprint (SHA-256 of the SubjectPublicKeyInfo) and the private key as PKCS#8. The caller clears it.</summary>
    public (string Fingerprint, byte[] Pkcs8) ReadBackupKey(string file)
    {
        var xml = Unprotect(File.ReadAllBytes(file), "a backup key");
        try
        {
            using var rsa = RSA.Create();
            rsa.FromXmlString(Encoding.UTF8.GetString(xml));
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
            return (fingerprint, rsa.ExportPkcs8PrivateKey());
        }
        catch (CryptographicException)
        {
            throw new AgentFailure(409, "legacy-backup-key", "An old backup key file does not hold an RSA key.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(xml);
        }
    }

    // ---- other ----

    /// <summary>The old client's SSH private key for root on a VM (<c>~\.ssh\&lt;keyName&gt;</c>).</summary>
    public string KeyFile(LegacyInstance instance) => Path.Combine(Home, ".ssh", instance.KeyName);

    private byte[] Unprotect(byte[] data, string what)
    {
        if (_protection is null) throw new AgentFailure(501, "legacy-protection", $"The old client's protected files ({what}) can only be opened on Windows by the user who wrote them.");
        try { return _protection.Unprotect(data); }
        catch (CryptographicException) { throw new AgentFailure(409, "legacy-unreadable", $"This Windows account cannot open {what} of the old client."); }
    }

    public static JsonObject? ReadObject(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path).TrimStart('﻿')) as JsonObject : null; }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    private static string? Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}

public sealed record LegacySecret(string Name, string Description, string? Username, string Value, bool AutoApprove)
{
    public override string ToString() => $"LegacySecret {{ Name = {Name} }}";
}

/// <summary>The old vault: its key <c>K</c>, its secrets, and how many leases and cleanups it held (they are not carried over).</summary>
public sealed record LegacyVault(byte[]? Key, IReadOnlyList<LegacySecret> Secrets, int Leases, int Cleanups)
{
    public override string ToString() => $"LegacyVault {{ Secrets = {Secrets.Count} }}";
}
