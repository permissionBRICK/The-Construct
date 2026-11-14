using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Agent.Device;
using Construct.Agent.Update;
using Construct.Agent.Upgrade;

namespace Construct.Agent.Engines;

/// <summary>Where setting up this PC's engine stands.</summary>
/// <param name="State"><c>idle</c>, <c>running</c>, <c>done</c>, <c>failed</c> (the installer's reason in <paramref name="Message"/>) or <c>restart</c> (Windows must restart to turn on Hyper-V).</param>
/// <param name="Engine">The engine set up, once <c>done</c>.</param>
public sealed record EngineSetupView(bool Available, string State, string? Message, string? Engine);

/// <summary>
/// The engine on this PC (5.3), set up the same way for a first run and for the one-time upgrade's engine step:
/// the host package from this device's channel, checked against the manifest's SHA-256, installed by
/// <c>Install-ConstructHost.ps1 -Solo</c> with one elevation prompt, unattended and without a window: what the
/// installer prints goes to <c>engine-install.log</c>, its outcome to <c>engine-install.json</c> (done, failed
/// with the reason, or restart). The installer writes the user's admin token into a file only this user can
/// read; it is taken into the state (protected) and deleted. Nothing restarts Windows but <see cref="RestartAsync"/>,
/// which the user's click calls, and only after the installer said a restart is needed.
/// </summary>
public sealed class LocalEngineSetup : IDisposable
{
    public const string TokenFileName = "engine-token.json";
    public const string ResultFileName = "engine-install.json";
    public const string LogFileName = "engine-install.log";

    private readonly DeviceAgent _agent;
    private readonly AgentOptions _options;
    private readonly IUpgradePlatform _platform;
    private readonly DeviceSettings _settings;
    private readonly object _gate = new();
    private Task _work = Task.CompletedTask;
    private bool _running;
    private string _state = "idle";
    private string? _message;
    private string? _engine;

    public LocalEngineSetup(DeviceAgent agent, AgentOptions options, IUpgradePlatform platform, DeviceSettings settings)
    {
        _agent = agent;
        _options = options;
        _platform = platform;
        _settings = settings;
        Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>Downloads the manifest and the package; tests replace it.</summary>
    public HttpClient Http { get; set; }

    /// <summary>Replaces download and installer with a function that leaves the token file and returns its path; the upgrade's tests use it.</summary>
    public Func<CancellationToken, Task<string>>? Installer { get; set; }

    /// <summary>Whether this device can host an engine at all (Windows).</summary>
    public bool Available => _platform.CanHostEngine;

    /// <summary>The setup started from the route, awaitable.</summary>
    public Task Work
    {
        get { lock (_gate) return _work; }
    }

    private string TokenFile => Path.Combine(_options.DataDirectory, TokenFileName);

    private string ResultFile => Path.Combine(_options.DataDirectory, ResultFileName);

    private string LogFile => Path.Combine(_options.DataDirectory, LogFileName);

    public EngineSetupView View()
    {
        lock (_gate) return new EngineSetupView(Available, _running ? "running" : _state, _running ? null : _message, _engine);
    }

    /// <summary>
    /// Starts the setup for a first run (202): an engine this agent already added on loopback is taken as it is;
    /// otherwise the installer runs. Progress as <c>engine-setup</c> events.
    /// </summary>
    public EngineSetupView Start()
    {
        if (!Available) throw new AgentFailure(501, "not-available", "Only a Windows PC can run an engine.");
        lock (_gate)
        {
            if (_running) throw new AgentFailure(409, "engine-setup-busy", "The engine is being set up.");
            _running = true;
            _work = Task.Run(async () =>
            {
                try
                {
                    var known = _agent.EngineIdFor($"https://127.0.0.1:{_options.LocalEnginePort}");
                    if (known is null || _agent.Engines().First(e => e.Id == known) is { SignedIn: false, Enrolled: false })
                        await RunLockedAsync(null, CancellationToken.None).ConfigureAwait(false);
                    else Finish("done", null, known);
                }
                catch (Exception ex) when (Expected(ex))
                {
                    // Recorded by the run itself; the view shows it.
                }
                finally
                {
                    lock (_gate)
                    {
                        _running = false;
                        // Ended by something the run did not expect: a failure with Retry, never "running" forever.
                        if (_state == "running") { _state = "failed"; _message = "Setting up the engine failed; retry."; }
                    }

                    _agent.Events.Publish("engine-setup");
                }
            });
        }

        _agent.Events.Publish("engine-setup");
        return View();
    }

    /// <summary>
    /// The setup itself, for a caller with its own step (the upgrade): the engine's id, or an <see cref="AgentFailure"/>
    /// (<c>restart-needed</c> when Windows must restart first). <paramref name="bootstrapKey"/> is the old client's key.
    /// </summary>
    public async Task<string> RunAsync(string? bootstrapKey, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_running) throw new AgentFailure(409, "engine-setup-busy", "The engine is being set up.");
            _running = true;
        }

        try
        {
            return await RunLockedAsync(bootstrapKey, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
                if (_state == "running") { _state = "failed"; _message = "Setting up the engine failed; retry."; }
            }

            _agent.Events.Publish("engine-setup");
        }
    }

    private async Task<string> RunLockedAsync(string? bootstrapKey, CancellationToken ct)
    {
        lock (_gate) { _state = "running"; _message = null; _engine = null; }
        _agent.Events.Publish("engine-setup");
        try
        {
            // A token file left by an earlier run that could not take it in is taken now, without a second prompt.
            var tokenFile = TokenFile;
            if (!File.Exists(tokenFile))
            {
                if (Installer is not null) tokenFile = await Installer(ct).ConfigureAwait(false);
                else await InstallAsync(bootstrapKey, ct).ConfigureAwait(false);
            }

            if (!File.Exists(tokenFile)) throw new AgentFailure(502, "engine-install-failed", "The engine installer wrote no token; see engine-install.log in the agent's folder, then retry.");
            string url, pin;
            string? token;
            try
            {
                var document = JsonNode.Parse(File.ReadAllText(tokenFile)) as JsonObject ?? throw new JsonException();
                url = document["url"]?.GetValue<string>() ?? $"https://127.0.0.1:{_options.LocalEnginePort}";
                pin = (document["certificateSha256"]?.GetValue<string>() ?? "").Replace(":", "", StringComparison.Ordinal);
                token = document["token"]?.GetValue<string>();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                File.Delete(tokenFile);
                throw new AgentFailure(502, "engine-install-failed", "The engine's token file is not readable; retry.");
            }

            // Only an engine on this PC: the file names loopback, as the solo installer writes it.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || !parsed.IsLoopback)
            {
                File.Delete(tokenFile);
                throw new AgentFailure(502, "engine-install-failed", "The engine's token file names no engine on this PC.");
            }

            // Kept until the engine took it: a retry while the new engine still starts needs no second prompt.
            var id = (await _agent.AddEngineAsync(url, pin, "This PC", token, ct).ConfigureAwait(false)).Id;
            File.Delete(tokenFile);
            Finish("done", null, id);
            return id;
        }
        catch (AgentFailure failure) when (failure.Code == "restart-needed")
        {
            Finish("restart", failure.Message, null);
            throw;
        }
        catch (Exception ex) when (Expected(ex))
        {
            Finish("failed", Reason(ex), null);
            throw;
        }
    }

    internal static bool Expected(Exception ex) => ex is AgentFailure or HttpRequestException or IOException or JsonException or InvalidDataException
        or UnauthorizedAccessException or TaskCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception;

    private void Finish(string state, string? message, string? engine)
    {
        lock (_gate) { _state = state; _message = message; _engine = engine; }
    }

    private static string Reason(Exception ex) => ex is AgentFailure ? ex.Message : $"Setting up the engine failed ({ex.GetType().Name}); retry.";

    /// <summary>Restarts Windows, at the user's click, when the installer said Hyper-V needs it.</summary>
    public async Task RestartAsync(CancellationToken ct)
    {
        lock (_gate)
            if (_running || _state != "restart") throw new AgentFailure(409, "no-restart-needed", "Nothing here needs Windows to restart.");
        await _platform.RestartWindowsAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() => Http.Dispose();

    private async Task InstallAsync(string? bootstrapKey, CancellationToken ct)
    {
        if (!Available) throw new AgentFailure(501, "not-available", "Only a Windows PC can run an engine.");
        var channel = _settings.Read().Channel;
        var manifestUrl = AgentUpdater.ManifestUrl(channel, AgentUpdater.DefaultRepository) ?? throw new AgentFailure(400, "invalid-channel", "The device's channel is not usable.");
        var manifest = JsonNode.Parse(await Http.GetStringAsync(manifestUrl, ct).ConfigureAwait(false)) as JsonObject
            ?? throw new AgentFailure(502, "manifest-unavailable", "The channel manifest could not be read.");
        var asset = manifest["payloadAsset"]?.GetValue<string>();
        var sha = manifest["payloadSha256"]?.GetValue<string>();
        var tag = manifest["releaseTag"]?.GetValue<string>();
        if (asset is null || sha is null || tag is null || asset.Contains('/') || asset.Contains('\\') || sha.Length != 64 || !sha.All(Uri.IsHexDigit))
            throw new AgentFailure(502, "manifest-invalid", "The channel manifest names no host package.");
        var url = channel is "stable" or "nightly"
            ? new Uri($"https://github.com/{AgentUpdater.DefaultRepository}/releases/download/{tag}/{Uri.EscapeDataString(asset)}")
            : new Uri(manifestUrl, Uri.EscapeDataString(asset));
        var zip = Path.Combine(_options.DataDirectory, "engine-package.zip");
        try
        {
            await using (var file = File.Create(zip))
            await using (var body = await Http.GetStreamAsync(url, ct).ConfigureAwait(false))
                await body.CopyToAsync(file, ct).ConfigureAwait(false);
            await using (var check = File.OpenRead(zip))
                if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false)).Equals(sha, StringComparison.OrdinalIgnoreCase))
                    throw new AgentFailure(502, "package-mismatch", "The host package does not match the SHA-256 in the channel manifest.");
            using (var archive = ZipFile.OpenRead(zip))
                if (archive.GetEntry("scripts/service/host/Install-ConstructHost.ps1") is null) throw new AgentFailure(502, "package-invalid", "The host package has no installer.");

            File.Delete(ResultFile);
            var payload = new JsonObject
            {
                ["zip"] = zip, ["sha256"] = sha.ToLowerInvariant(), ["tokenFile"] = TokenFile, ["resultFile"] = ResultFile, ["log"] = LogFile,
                ["adminUser"] = Environment.UserDomainName + "\\" + Environment.UserName, ["bootstrapKeyPath"] = bootstrapKey ?? "",
            };
            var exit = await _platform.RunElevatedAsync("powershell.exe", ElevatedArguments(payload), ct).ConfigureAwait(false);
            ReadResult(exit);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    /// <summary>The installer's outcome: done, or an <see cref="AgentFailure"/> with its reason (<c>restart-needed</c> for a restart).</summary>
    private void ReadResult(int exit)
    {
        JsonObject? result = null;
        try
        {
            if (File.Exists(ResultFile)) result = JsonNode.Parse(File.ReadAllText(ResultFile)) as JsonObject;
        }
        catch (JsonException)
        {
        }

        var message = result?["message"] is JsonValue m && m.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
        switch (result?["result"] is JsonValue r && r.TryGetValue<string>(out var outcome) ? outcome : null)
        {
            case "done": return;
            case "restart": throw new AgentFailure(409, "restart-needed", message ?? "Windows must restart to turn on Hyper-V.");
            case "failed": throw new AgentFailure(502, "engine-install-failed", message ?? "The engine installer failed; see engine-install.log in the agent's folder.");
            default:
                throw new AgentFailure(502, "engine-install-failed", exit == 0
                    ? "The engine installer gave no result; see engine-install.log in the agent's folder."
                    : $"The engine installer ended with exit code {exit}; see engine-install.log in the agent's folder.");
        }
    }

    /// <summary>
    /// PowerShell's arguments for the elevated run: hidden, never asking (<c>-NonInteractive</c> makes any prompt fail
    /// instead of waiting), and the script itself on the command line (<c>-EncodedCommand</c>) with its values as inert
    /// base64 inside it, so no file this user can write is run elevated.
    /// </summary>
    public static IReadOnlyList<string> ElevatedArguments(JsonObject payload)
    {
        var values = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var script = ElevatedScript.Replace("__PAYLOAD__", values, StringComparison.Ordinal);
        return ["-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))];
    }

    /// <summary>
    /// The elevated part. It opens the package once, denying writes, checks the hash on that handle and unpacks from it,
    /// into <c>%ProgramData%\Construct\host</c>, which it first makes writable by SYSTEM and Administrators only (as is
    /// <c>%ProgramData%\Construct</c>), and runs the solo install unattended. Its output goes to the log, its outcome to
    /// the result file; an error of its own is written there as <c>failed</c> when the installer wrote nothing.
    /// </summary>
    private const string ElevatedScript = """
        $ErrorActionPreference = 'Stop'
        $p = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__PAYLOAD__')) | ConvertFrom-Json
        function Write-Result([string]$Result, [string]$Message) {
            $json = [ordered]@{ result = $Result; message = $Message } | ConvertTo-Json -Compress
            [IO.File]::WriteAllText($p.resultFile, $json, (New-Object Text.UTF8Encoding($false)))
        }
        function Protect-Directory([string]$Path) {
            if (-not (Test-Path -LiteralPath $Path)) { New-Item -ItemType Directory -Path $Path | Out-Null }
            $acl = New-Object Security.AccessControl.DirectorySecurity
            $acl.SetAccessRuleProtection($true, $false)
            $admins = New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'
            foreach ($rule in @(@('S-1-5-18', 'FullControl'), @('S-1-5-32-544', 'FullControl'), @('S-1-5-32-545', 'ReadAndExecute'))) {
                $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $rule[0]), $rule[1], 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
            }
            $acl.SetOwner($admins)
            Set-Acl -LiteralPath $Path -AclObject $acl
        }
        try {
            $root = Join-Path $env:ProgramData 'Construct'
            $dir = Join-Path $root 'host'
            Protect-Directory $root
            if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
            Protect-Directory $dir
            Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
            $stream = [IO.File]::Open($p.zip, 'Open', 'Read', 'Read')
            try {
                $sha = [Security.Cryptography.SHA256]::Create()
                $hash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
                if ($hash -ne $p.sha256) { throw 'The host package changed after it was checked.' }
                $stream.Position = 0
                $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
                try { [IO.Compression.ZipFileExtensions]::ExtractToDirectory($archive, $dir) } finally { $archive.Dispose() }
            } finally { $stream.Dispose() }
            $installer = Join-Path $dir 'scripts\service\host\Install-ConstructHost.ps1'
            $arguments = @{ Solo = $true; SkipIsoBuild = $true; TokenFile = $p.tokenFile; ResultFile = $p.resultFile; AdminUser = $p.adminUser
                            ScriptsDir = (Join-Path $dir 'scripts'); PublishDir = (Join-Path $dir 'service') }
            if ($p.bootstrapKeyPath) { $arguments.BootstrapKeyPath = $p.bootstrapKeyPath }
            $global:LASTEXITCODE = 0
            & $installer @arguments *>&1 | Out-File -LiteralPath $p.log -Encoding utf8
            $code = $LASTEXITCODE
            if (-not (Test-Path -LiteralPath $p.resultFile)) { Write-Result 'failed' "The installer ended with exit code $code and no result." }
            exit $code
        } catch {
            try { ($_ | Out-String) | Out-File -LiteralPath $p.log -Append -Encoding utf8 } catch { }
            if (-not (Test-Path -LiteralPath $p.resultFile)) { Write-Result 'failed' (($_.Exception.Message -split "`r?`n")[0]) }
            exit 1
        }
        """;
}
