using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Agent.Device;
using Construct.Agent.Update;

namespace Construct.Agent.Upgrade;

/// <summary>Where the one-time upgrade stands, for the dialog (11.6).</summary>
/// <param name="Legacy">True when the old client's state is on this PC: the dialog shows the upgrade variant.</param>
/// <param name="Steps">The dialog's steps in order, each <c>todo</c>, <c>running</c>, <c>done</c>, <c>skipped</c> or <c>failed</c>.</param>
/// <param name="Found">What the converter will read (counts and names, never a value).</param>
public sealed record UpgradeView(bool Legacy, string Stage, IReadOnlyList<UpgradeStep> Steps, UpgradeFound? Found, string? StoreEngine, string? LocalEngine,
    IReadOnlyList<UpgradeItem> Items, IReadOnlyList<string> Notes, IReadOnlyList<string> OldWriters, bool Running, string? Error);

public sealed record UpgradeStep(string Id, string State, string? Message = null);

/// <summary>The old state the converter will copy.</summary>
public sealed record UpgradeFound(string? InstallFolder, IReadOnlyList<string> OtherInstallFolders, IReadOnlyList<string> LocalVms, IReadOnlyList<string> RemoteVms,
    IReadOnlyList<string> Hosts, IReadOnlyList<string> Profiles, int UnmergedChanges, bool Vault, int BackupKeys, bool Upstream, string Channel);

/// <summary><c>POST /api/upgrade/convert</c>: the previous install folder, when not the one found.</summary>
public sealed record ConvertRequest(string? From = null);

/// <summary>
/// The upgrade dialog's steps as agent routes (11.6, 13.2), in order: the engine (installed elevated where
/// this PC has a local VM; the shared hosts added with their pins and tokens), the recovery code (the
/// existing first-device setup, which takes the old vault key as <c>K</c>), the old writers closed, the
/// conversion. Each step can be repeated; the state is in <c>bridge.json</c>, whose <c>converted</c> stage
/// is written last by the converter.
/// </summary>
public sealed class UpgradeService : IDisposable
{
    private readonly DeviceAgent _agent;
    private readonly AgentOptions _options;
    private readonly IUpgradePlatform _platform;
    private readonly DeviceSettings _settings;
    private readonly IDeviceEngines _engines;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private Task _work = Task.CompletedTask;
    private string? _running;
    private string? _error;

    public UpgradeService(DeviceAgent agent, AgentOptions options, IUpgradePlatform platform, DeviceSettings settings, IDeviceEngines engines, HttpClient? http = null)
    {
        _agent = agent;
        _options = options;
        _platform = platform;
        _settings = settings;
        _engines = engines;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        Bridge = new BridgeFile(Path.Combine(options.DataDirectory, "bridge.json"));
        // The first-device setup of an upgrade takes the old vault key as K (8.2, 13.1).
        agent.SetupVaultKey = SetupVaultKey;
    }

    public BridgeFile Bridge { get; }

    /// <summary>Tests replace the converter's knobs (a cut in the middle).</summary>
    public Func<string, Task>? BeforeItem { get; set; }

    /// <summary>The package the engine step installs; tests replace it.</summary>
    public Func<CancellationToken, Task<string>>? EngineInstaller { get; set; }

    /// <summary>The running step, awaitable.</summary>
    public Task Work
    {
        get { lock (_gate) return _work; }
    }

    private LegacyState? Legacy(string? from = null)
    {
        if (_platform.LocalAppData is not { } local) return null;
        var state = LegacyState.Open(local, from ?? Bridge.Read().From, _platform.Home, _platform.LegacyProtection);
        return state.Found ? state : null;
    }

    private byte[]? SetupVaultKey()
    {
        if (Bridge.Read().Stage == BridgeState.Converted || Legacy() is not { HasVault: true } legacy) return null;
        return legacy.ReadVault().Key;
    }

    // ---- the view ----

    public UpgradeView View()
    {
        var bridge = Bridge.Read();
        LegacyState? legacy;
        string? error;
        lock (_gate) error = _error;
        try { legacy = Legacy(); }
        catch (AgentFailure failure) { legacy = null; error ??= failure.Message; }

        var writers = bridge.Stage == BridgeState.Converted ? [] : OldWriters();
        var steps = new List<UpgradeStep>
        {
            Step("engine", bridge.Engine is "done" or "skipped" ? bridge.Engine : "todo"),
            Step("recovery", _agent.IsRegistered ? "done" : "todo"),
            Step("close", bridge.Stage == BridgeState.Converted ? "done" : writers.Count == 0 ? "done" : "todo", writers.Count == 0 ? null : string.Join(", ", writers) + " still open."),
            Step("convert", bridge.Stage == BridgeState.Converted ? "done" : bridge.Items.Any(i => i.State == "failed") ? "failed" : "todo"),
        };
        return new UpgradeView(legacy is not null, bridge.Stage, steps, legacy is null ? null : Found(legacy), bridge.StoreEngine ?? _agent.StoreEngineId, bridge.LocalEngine,
            bridge.Items, bridge.Notes, writers, _running is not null, error);

        UpgradeStep Step(string id, string state, string? message = null) =>
            _running == id ? new UpgradeStep(id, "running") : new UpgradeStep(id, state, message);
    }

    private UpgradeFound Found(LegacyState legacy)
    {
        var instances = legacy.Instances();
        var repo = new LegacyConfigRepo(legacy.ConfigRepository);
        return new UpgradeFound(legacy.InstallFolder, legacy.OtherInstallFolders, instances.Where(i => i.Local).Select(i => i.Name).ToArray(),
            instances.Where(i => !i.Local).Select(i => i.Name).ToArray(), legacy.Hosts().Select(h => h.Url).ToArray(),
            repo.Exists ? repo.Profiles().Keys.ToArray() : [], repo.Exists ? repo.UnmergedChanges(instances.Select(i => i.ConfigBranch)).Count : 0, legacy.HasVault,
            legacy.BackupKeyFiles().Count, repo.Exists && repo.UpstreamFiles().Count > 0, legacy.Channel());
    }

    // ---- step 1: the engine ----

    /// <summary>Starts the engine step: the solo engine where this PC has a local VM, and the shared hosts added for sign-in.</summary>
    public UpgradeView StartEngine() => Start("engine", EngineAsync);

    private async Task EngineAsync(CancellationToken ct)
    {
        var legacy = Legacy() ?? throw new AgentFailure(409, "no-legacy", "No old Construct install was found on this PC.");
        var local = legacy.Instances().Any(i => i.Local);
        string? localEngine = Bridge.Read().LocalEngine;
        if (local && (localEngine is null || _agent.Engines().All(e => e.Id != localEngine)))
            localEngine = await InstallSoloEngineAsync(legacy, ct).ConfigureAwait(false);

        // The shared hosts are added for sign-in now; the converter enrols this device there.
        string? firstHost = null;
        foreach (var host in legacy.Hosts().Where(h => h.Pin is not null))
        {
            if (_agent.EngineIdFor(host.Url) is { } known) { firstHost ??= known; continue; }
            var token = host.HasToken ? legacy.HostToken(host) : null;
            var added = await _agent.AddEngineAsync(host.Url, host.Pin!, null, token, ct).ConfigureAwait(false);
            if (token is not null) firstHost ??= added.Id;
        }

        var store = _agent.StoreEngineId ?? localEngine ?? firstHost
            ?? throw new AgentFailure(409, "no-engine", "No engine to keep the account's store: this PC has no local VM and no shared host with a stored sign-in.");
        Bridge.Update(b => b with { Engine = local ? "done" : "skipped", LocalEngine = localEngine, StoreEngine = store });
    }

    /// <summary>
    /// The solo engine (5.3): the host package from this device's channel, checked against the manifest's
    /// SHA-256, installed by <c>Install-ConstructHost.ps1 -Solo</c> with one elevation prompt; the installer
    /// writes the user's admin token into a file this user alone can read, which is taken into the state
    /// (protected) and deleted.
    /// </summary>
    private async Task<string> InstallSoloEngineAsync(LegacyState legacy, CancellationToken ct)
    {
        var tokenFile = Path.Combine(_options.DataDirectory, "engine-token.json");
        if (!File.Exists(tokenFile))
        {
            if (EngineInstaller is not null) tokenFile = await EngineInstaller(ct).ConfigureAwait(false);
            else await RunSoloInstallerAsync(legacy, tokenFile, ct).ConfigureAwait(false);
        }

        if (!File.Exists(tokenFile)) throw new AgentFailure(502, "engine-install-failed", "The engine installer wrote no token; see its window for why, then retry.");
        var document = JsonNode.Parse(File.ReadAllText(tokenFile)) as JsonObject ?? throw new AgentFailure(502, "engine-install-failed", "The engine's token file is not readable.");
        var url = document["url"]?.GetValue<string>() ?? $"https://127.0.0.1:{_options.LocalEnginePort}";
        var pin = (document["certificateSha256"]?.GetValue<string>() ?? "").Replace(":", "", StringComparison.Ordinal);
        var token = document["token"]?.GetValue<string>();
        var added = await _agent.AddEngineAsync(url, pin, "This PC", token, ct).ConfigureAwait(false);
        File.Delete(tokenFile);
        return added.Id;
    }

    private async Task RunSoloInstallerAsync(LegacyState legacy, string tokenFile, CancellationToken ct)
    {
        var channel = _settings.Read().Channel;
        var manifestUrl = AgentUpdater.ManifestUrl(channel, AgentUpdater.DefaultRepository) ?? throw new AgentFailure(400, "invalid-channel", "The device's channel is not usable.");
        var manifest = JsonNode.Parse(await _http.GetStringAsync(manifestUrl, ct).ConfigureAwait(false)) as JsonObject
            ?? throw new AgentFailure(502, "manifest-unavailable", "The channel manifest could not be read.");
        var asset = manifest["payloadAsset"]?.GetValue<string>();
        var sha = manifest["payloadSha256"]?.GetValue<string>();
        var tag = manifest["releaseTag"]?.GetValue<string>();
        if (asset is null || sha is null || tag is null || asset.Contains('/') || asset.Contains('\\') || sha.Length != 64)
            throw new AgentFailure(502, "manifest-invalid", "The channel manifest names no host package.");
        var url = channel is "stable" or "nightly"
            ? new Uri($"https://github.com/{AgentUpdater.DefaultRepository}/releases/download/{tag}/{Uri.EscapeDataString(asset)}")
            : new Uri(manifestUrl, Uri.EscapeDataString(asset));
        var zip = Path.Combine(_options.DataDirectory, "engine-package.zip");
        await using (var file = File.Create(zip))
        await using (var body = await _http.GetStreamAsync(url, ct).ConfigureAwait(false))
            await body.CopyToAsync(file, ct).ConfigureAwait(false);
        await using (var check = File.OpenRead(zip))
            if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false)).Equals(sha, StringComparison.OrdinalIgnoreCase))
                throw new AgentFailure(502, "package-mismatch", "The host package does not match the SHA-256 in the channel manifest.");
        using (var archive = ZipFile.OpenRead(zip))
            if (archive.GetEntry("scripts/service/host/Install-ConstructHost.ps1") is null) throw new AgentFailure(502, "package-invalid", "The host package has no installer.");

        // The elevated part checks the hash again (the zip sits in this user's directory), unpacks the package
        // under ProgramData, where the service may run from, and runs the solo install.
        var script = Path.Combine(_options.DataDirectory, "install-engine.ps1");
        File.WriteAllText(script, SoloScript);
        var arguments = new List<string>
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Zip", zip, "-Sha256", sha, "-TokenFile", tokenFile,
            "-AdminUser", Environment.UserDomainName + "\\" + Environment.UserName,
        };
        if (legacy.BootstrapKey() is { } key) arguments.AddRange(["-BootstrapKeyPath", key]);
        var exit = await _platform.RunElevatedAsync("powershell.exe", arguments, ct).ConfigureAwait(false);
        File.Delete(zip);
        if (exit != 0) throw new AgentFailure(502, "engine-install-failed", $"The engine installer ended with exit code {exit}; see its window for why, then retry.");
    }

    private const string SoloScript = """
        param([string]$Zip, [string]$Sha256, [string]$TokenFile, [string]$AdminUser, [string]$BootstrapKeyPath = '')
        $ErrorActionPreference = 'Stop'
        try {
            if ((Get-FileHash -Algorithm SHA256 -LiteralPath $Zip).Hash.ToLowerInvariant() -ne $Sha256.ToLowerInvariant()) { throw 'The host package changed after it was checked.' }
            $dir = Join-Path $env:ProgramData 'Construct\host'
            if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            [IO.Compression.ZipFile]::ExtractToDirectory($Zip, $dir)
            $installer = Join-Path $dir 'scripts\service\host\Install-ConstructHost.ps1'
            $arguments = @{ Solo = $true; SkipIsoBuild = $true; TokenFile = $TokenFile; AdminUser = $AdminUser; ScriptsDir = (Join-Path $dir 'scripts'); PublishDir = (Join-Path $dir 'service') }
            if ($BootstrapKeyPath) { $arguments.BootstrapKeyPath = $BootstrapKeyPath }
            & $installer @arguments
            exit 0
        } catch {
            Write-Host ('The engine install failed: ' + $_.Exception.Message) -ForegroundColor Red
            Read-Host 'Press Enter to close' | Out-Null
            exit 1
        }
        """;

    // ---- step 3: the old writers ----

    /// <summary>
    /// VS Code and the old Companion (the platform's view), and a lifecycle script of the old client still running:
    /// it holds <c>lifecycle-running.json</c> with its pid until it ends (<c>lib/AgentVm.LifecycleRun.ps1</c>).
    /// </summary>
    public IReadOnlyList<string> OldWriters()
    {
        var open = _platform.OldWriters().ToList();
        if (_platform.LocalAppData is { } local &&
            LegacyState.ReadObject(Path.Combine(local, "The-Construct", "lifecycle-running.json")) is { } run &&
            run["pid"] is JsonValue pid && pid.TryGetValue<int>(out var id) && Alive(id))
            open.Add("A Construct script (" + (run["action"]?.GetValue<string>() ?? "running") + ")");
        return open;

        static bool Alive(int id)
        {
            try { using var p = System.Diagnostics.Process.GetProcessById(id); return !p.HasExited; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
    }

    // ---- step 4: the conversion ----

    /// <summary>Starts the conversion; refused while VS Code or the old Companion still runs, or before the recovery code is set.</summary>
    public UpgradeView StartConvert(ConvertRequest request)
    {
        if (!_agent.IsRegistered) throw new AgentFailure(409, "not-registered", "Set up the recovery code first.");
        if (Bridge.Read().Stage != BridgeState.Converted && OldWriters() is { Count: > 0 } open)
            throw new AgentFailure(409, "writers-open", "Close these first: " + string.Join(", ", open) + ".");
        if (request.From is { Length: > 0 } from) Bridge.Update(b => b with { From = Path.GetFullPath(from) });
        return Start("convert", ConvertAsync);
    }

    /// <summary>The conversion itself (the CLI runs it in the foreground); true when <c>converted</c>.</summary>
    public async Task<bool> ConvertAsync(CancellationToken ct, Action<UpgradeItem>? progress = null)
    {
        var legacy = Legacy() ?? throw new AgentFailure(409, "no-legacy", "No old Construct install was found on this PC.");
        var converter = new LegacyConverter(_agent, _engines, _settings, Bridge, _platform, _options.DataDirectory, item =>
        {
            progress?.Invoke(item);
            _agent.Events.Publish("upgrade", null, JsonSerializer.SerializeToNode(item, Http.AgentJson.Options));
        }) { BeforeItem = BeforeItem };
        return await converter.RunAsync(legacy, ct).ConfigureAwait(false);
    }

    private Task ConvertAsync(CancellationToken ct) => ConvertAsync(ct, null);

    // ---- plumbing ----

    private UpgradeView Start(string step, Func<CancellationToken, Task> work)
    {
        lock (_gate)
        {
            if (_running is not null) throw new AgentFailure(409, "upgrade-busy", $"The step '{_running}' is still running.");
            _running = step;
            _error = null;
            _work = Task.Run(async () =>
            {
                try
                {
                    await work(CancellationToken.None).ConfigureAwait(false);
                }
                catch (AgentFailure failure)
                {
                    lock (_gate) _error = failure.Message;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException or UnauthorizedAccessException or TaskCanceledException)
                {
                    lock (_gate) _error = $"The step '{step}' failed ({ex.GetType().Name}); retry.";
                }
                finally
                {
                    lock (_gate) _running = null;
                    _agent.Events.Publish("upgrade");
                }
            });
        }

        _agent.Events.Publish("upgrade");
        return View();
    }

    public void Dispose() => _http.Dispose();
}
