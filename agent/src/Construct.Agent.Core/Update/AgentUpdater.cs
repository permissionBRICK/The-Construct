using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Agent.Device;

namespace Construct.Agent.Update;

/// <summary>One package of a channel release (docs/release-flow.md): where to fetch it and its SHA-256.</summary>
public sealed record AgentPackage(string Version, Uri Url, string Sha256);

/// <summary>The device's update state for the UI (12.1).</summary>
/// <param name="Available">The version on offer, or null.</param>
/// <param name="State"><c>idle</c>, <c>checking</c>, <c>waiting</c> (for the agent to be idle), <c>downloading</c>, <c>installing</c>, <c>failed</c>.</param>
/// <param name="Installed">False for a build that does not run from an installed package: it cannot update itself.</param>
/// <param name="Waiting">While <c>waiting</c>: what the install waits for to end (<c>the update of hosts and VMs</c>, <c>a device pairing</c>).</param>
public sealed record UpdateView(string Version, string Channel, string? Available, string State, string? Error, bool Installed, DateTimeOffset? CheckedAt,
    string? Waiting = null);

/// <summary>Starts an installed agent executable detached from this one (the switch and the way back).</summary>
public interface IAgentLauncher
{
    /// <param name="surfaces">The shell surfaces the new process opens (<c>pop</c>, <c>win</c>); none starts it in the background.</param>
    Task LaunchAsync(string executable, IReadOnlyList<string> surfaces, CancellationToken ct);
}

/// <summary>
/// The agent updating itself (12.1, 12.2, S20): it reads its channel's manifest, downloads its own
/// package, checks the SHA-256 the manifest names, unpacks it beside the running version and, when
/// the agent is idle, switches: <c>install.json</c> names the new version as current and pending and
/// the running one as previous, the new one is started and this one stops. The new one confirms itself
/// with a self-test at start (<see cref="StartupAsync"/>); when that fails it starts the previous one
/// again. Nothing here installs an engine or a VM; "update everything" asks them.
/// </summary>
public sealed class AgentUpdater : IAsyncDisposable
{
    public const string DefaultRepository = "permissionBRICK/The-Construct";

    private readonly AgentOptions _options;
    private readonly AgentInstall _install;
    private readonly DeviceSettings _settings;
    private readonly HttpClient _http;
    private readonly string _packageKey;
    private readonly string _repository;
    private readonly Func<string?> _busy;
    private readonly IAgentLauncher _launcher;
    private readonly Action _stop;
    private readonly Action _changed;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private AgentPackage? _offer;
    private string _state = "idle";
    private string? _error;
    private DateTimeOffset? _checkedAt;
    private string? _waiting;
    private Task _work = Task.CompletedTask;
    private Task _loop = Task.CompletedTask;

    /// <param name="packageKey">This build's key in the manifest's <c>packages</c>: <c>agentWindows</c> or <c>agentLinux</c>.</param>
    /// <param name="busy">What the user started that is still in flight (a pairing, "update everything"), in words; null when the agent is idle.</param>
    /// <param name="stop">Stops this agent after the switch.</param>
    public AgentUpdater(AgentOptions options, AgentInstall install, DeviceSettings settings, HttpClient http, string packageKey,
        Func<string?> busy, IAgentLauncher launcher, Action stop, Action changed, string repository = DefaultRepository)
    {
        _options = options;
        _install = install;
        _settings = settings;
        _http = http;
        _packageKey = packageKey;
        _busy = busy;
        _launcher = launcher;
        _stop = stop;
        _changed = changed;
        _repository = repository;
    }

    /// <summary>The installed version this process runs as; null for a development build.</summary>
    public string? Running { get; init; }

    /// <summary>How long the install waits for the agent to be idle before it goes ahead anyway (finding 21).</summary>
    public TimeSpan IdleLimit { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How often the install asks again whether the agent is idle.</summary>
    public TimeSpan IdlePoll { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a check counts as fresh for <see cref="CheckIfStale"/>.</summary>
    public TimeSpan FreshFor { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The surfaces of the desktop shell that are open (<c>pop</c>, <c>win</c>); the new version opens them again.</summary>
    public Func<IReadOnlyList<string>>? OpenSurfaces { get; set; }

    public UpdateView View()
    {
        lock (_gate) return new(_options.Version, _settings.Read().Channel, _offer?.Version, _state, _error, Running is not null, _checkedAt, _state == "waiting" ? _waiting : null);
    }

    public bool Available
    {
        get { lock (_gate) return _offer is not null; }
    }

    /// <summary>Checks the channel a minute after start and then every six hours; with automatic updates on, installs what it finds.</summary>
    public void Start() => _loop = LoopAsync();

    private async Task LoopAsync()
    {
        // The first check a minute after start, so a start (and a test) is not held up by the network.
        var wait = TimeSpan.FromMinutes(1);
        while (!_lifetime.IsCancellationRequested)
        {
            try { await Task.Delay(wait, _options.Time, _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            wait = TimeSpan.FromHours(6);
            try
            {
                await CheckAsync(_lifetime.Token).ConfigureAwait(false);
                if (_settings.Read().AutoUpdate && Available && Running is not null) StartInstall();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (AgentFailure) { }
        }
    }

    /// <summary>
    /// A check in the background unless one ran in the last <see cref="FreshFor"/> or one is under way: the pop-out
    /// asks for it when it opens, so a new release shows without waiting for the six-hourly check (finding 16).
    /// </summary>
    public void CheckIfStale()
    {
        lock (_gate)
        {
            if (_state is "checking" or "waiting" or "downloading" or "installing") return;
            if (_checkedAt is { } at && _options.Time.GetUtcNow() - at < FreshFor) return;
        }

        _ = Task.Run(async () =>
        {
            try { await CheckAsync(_lifetime.Token).ConfigureAwait(false); }
            catch (AgentFailure) { /* shown as the check's error */ }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Reads the channel's manifest; a different version than this one is on offer.</summary>
    public async Task<UpdateView> CheckAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_state is "waiting" or "downloading" or "installing") return View();
            _state = "checking";
            _error = null;
        }

        try
        {
            var package = await ReadManifestAsync(_settings.Read().Channel, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _offer = package.Version == _options.Version ? null : package;
                _state = "idle";
                _checkedAt = _options.Time.GetUtcNow();
            }
        }
        catch (AgentFailure failure)
        {
            lock (_gate)
            {
                _state = "failed";
                _error = failure.Message;
                // A failed check counts as one: the pop-out does not ask the network again on every open.
                _checkedAt = _options.Time.GetUtcNow();
            }

            throw;
        }
        finally
        {
            _changed();
        }

        return View();
    }

    /// <summary>Starts the install of the version on offer; it waits until the agent is idle.</summary>
    public UpdateView StartInstall()
    {
        lock (_gate)
        {
            if (Running is null) throw new AgentFailure(409, "not-installed", "This agent does not run from an installed package and cannot update itself.");
            if (_offer is null) throw new AgentFailure(409, "no-update", "No update is on offer; check first.");
            if (!_work.IsCompleted) return View();
            _state = "waiting";
            _error = null;
            _work = InstallAsync(_offer, _lifetime.Token);
        }

        _changed();
        return View();
    }

    /// <summary>The install itself; awaitable by tests.</summary>
    public Task Work
    {
        get { lock (_gate) return _work; }
    }

    private async Task InstallAsync(AgentPackage package, CancellationToken ct)
    {
        try
        {
            await WaitUntilIdleAsync(ct).ConfigureAwait(false);
            Set("downloading");
            var target = _install.DirectoryOf(package.Version);
            if (!Directory.Exists(target))
            {
                Directory.CreateDirectory(_install.VersionsDirectory);
                var download = Path.Combine(_install.VersionsDirectory, ".download-" + Guid.NewGuid().ToString("n")[..8]);
                try
                {
                    await DownloadCheckedAsync(package, download, ct).ConfigureAwait(false);
                    Set("installing");
                    var partial = target + ".partial";
                    if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
                    ZipFile.ExtractToDirectory(download, partial);
                    var executable = Path.Combine(partial, _install.ExecutableName);
                    if (!File.Exists(executable)) throw new AgentFailure(502, "package-invalid", $"The package has no {_install.ExecutableName}.");
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
                    Directory.Move(partial, target);
                }
                finally
                {
                    if (File.Exists(download)) File.Delete(download);
                }
            }

            Set("installing");
            await WaitUntilIdleAsync(ct).ConfigureAwait(false);
            _install.Write(new InstallState(package.Version, Running, Pending: true));
            // The surfaces open now open again in the new version (finding 5); the pop-out first, as the user left it.
            var surfaces = (OpenSurfaces?.Invoke() ?? []).Where(s => s is "pop" or "win").ToArray();
            await _launcher.LaunchAsync(_install.ExecutableOf(package.Version), surfaces, ct).ConfigureAwait(false);
            _stop();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is AgentFailure or IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException)
        {
            lock (_gate)
            {
                _state = "failed";
                _error = ex is AgentFailure failure ? failure.Message : "The update could not be installed.";
            }

            // The switch did not happen: the running version stays current.
            var state = _install.Read();
            if (state.Pending && state.Current == package.Version) _install.Write(new InstallState(Running, state.Previous == Running ? null : state.Previous));
            _changed();
        }
    }

    private void Set(string state)
    {
        lock (_gate) _state = state;
        _changed();
    }

    /// <summary>
    /// Waits while something the user started is in flight, saying what (finding 21), and no longer than
    /// <see cref="IdleLimit"/>: then the install goes ahead.
    /// </summary>
    private async Task WaitUntilIdleAsync(CancellationToken ct)
    {
        var until = _options.Time.GetUtcNow() + IdleLimit;
        string before;
        lock (_gate) before = _state;
        var waited = false;
        while (_busy() is { } what && _options.Time.GetUtcNow() < until)
        {
            lock (_gate)
            {
                _state = "waiting";
                _waiting = what;
            }

            waited = true;
            _changed();
            await Task.Delay(IdlePoll, _options.Time, ct).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _waiting = null;
            if (waited) _state = before;
        }
    }

    /// <summary>Downloads to <paramref name="path"/> and compares its SHA-256 with the manifest's; a mismatch deletes it.</summary>
    private async Task DownloadCheckedAsync(AgentPackage package, string path, CancellationToken ct)
    {
        using (var response = await _http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode) throw new AgentFailure(502, "download-failed", $"The package download answered {(int)response.StatusCode}.");
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
        }

        string actual;
        await using (var read = File.OpenRead(path)) actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(read, ct).ConfigureAwait(false));
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(package.Sha256.ToLowerInvariant())))
        {
            File.Delete(path);
            throw new AgentFailure(502, "hash-mismatch", "The downloaded package does not match the SHA-256 in the channel manifest.");
        }
    }

    /// <summary>This build's package in the channel's manifest.</summary>
    public async Task<AgentPackage> ReadManifestAsync(string channel, CancellationToken ct)
    {
        var url = ManifestUrl(channel, _repository) ?? throw new AgentFailure(400, "invalid-channel", "A channel is stable, nightly or an https manifest URL.");
        JsonNode? manifest;
        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new AgentFailure(502, "manifest-unavailable", $"The channel manifest answered {(int)response.StatusCode}.");
            manifest = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new AgentFailure(502, "manifest-unavailable", "The channel manifest could not be read.");
        }

        return Package(manifest, url, _packageKey, _repository, github: channel is "stable" or "nightly" or "main");
    }

    /// <summary>
    /// The package <paramref name="key"/> of a manifest: <c>version</c> (<c>X.Y.Z+sha</c>), and in <c>packages</c>
    /// its <c>asset</c> and <c>sha256</c>. A GitHub channel's assets are on the release the manifest names
    /// (<c>releaseTag</c>); a URL channel's beside the manifest (as the engine reads its own, ChannelReleases).
    /// </summary>
    public static AgentPackage Package(JsonNode? manifest, Uri manifestUrl, string key, string repository, bool github)
    {
        static AgentFailure Invalid(string why) => new(502, "manifest-invalid", "The channel manifest is invalid: " + why + ".");
        var version = manifest?["version"]?.GetValueKind() == JsonValueKind.String ? manifest["version"]!.GetValue<string>() : null;
        if (version is null || version.Length is 0 or > 100) throw Invalid("no version");
        if (manifest!["packages"]?[key] is not JsonObject package) throw Invalid($"no {key} package");
        var asset = package["asset"]?.GetValueKind() == JsonValueKind.String ? package["asset"]!.GetValue<string>() : "";
        var sha = package["sha256"]?.GetValueKind() == JsonValueKind.String ? package["sha256"]!.GetValue<string>() : "";
        if (asset.Length == 0 || asset.IndexOfAny(['/', '\\']) >= 0 || asset is "." or "..") throw Invalid("bad asset name");
        if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) throw Invalid("bad sha256");
        Uri url;
        if (github)
        {
            var tag = manifest["releaseTag"]?.GetValueKind() == JsonValueKind.String ? manifest["releaseTag"]!.GetValue<string>() : "";
            if (tag.Length == 0 || !tag.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_')) throw Invalid("no release tag");
            url = new Uri($"https://github.com/{repository}/releases/download/{tag}/{Uri.EscapeDataString(asset)}");
        }
        else
        {
            url = new Uri(manifestUrl, Uri.EscapeDataString(asset));
        }

        return new(version, url, sha.ToLowerInvariant());
    }

    public static Uri? ManifestUrl(string? channel, string repository) => channel switch
    {
        "stable" or "main" => new($"https://github.com/{repository}/releases/latest/download/manifest.json"),
        "nightly" => new($"https://github.com/{repository}/releases/download/channel-nightly/manifest.json"),
        _ when Uri.TryCreate(channel, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps && url.Host.Length > 0 => url,
        _ => null,
    };

    // ---- start of an installed version ----

    /// <summary>
    /// Starts the agent with <paramref name="startAndSelfTest"/> (the host comes up and answers its own
    /// health route). For the version the last switch made current and pending, a failure there writes
    /// the previous version back as current, starts it with <paramref name="launcher"/> and returns false;
    /// a pass confirms the version, prunes older ones and calls <paramref name="confirmed"/> with the
    /// version's directory (what follows the agent: the VS Code extension). Any other start just runs.
    /// </summary>
    public static async Task<bool> StartupAsync(AgentInstall install, string? running, Func<Task> startAndSelfTest, IAgentLauncher launcher,
        Func<Task>? waitForPrevious = null, Action<string>? confirmed = null, CancellationToken ct = default)
    {
        var state = install.Read();
        var trial = state.Pending && running is not null && running == state.Current;
        if (trial && waitForPrevious is not null) await waitForPrevious().ConfigureAwait(false);
        try
        {
            await startAndSelfTest().ConfigureAwait(false);
        }
        catch (Exception) when (trial && state.Previous is not null)
        {
            install.Write(new InstallState(state.Previous, null));
            await launcher.LaunchAsync(install.ExecutableOf(state.Previous), [], ct).ConfigureAwait(false);
            return false;
        }

        if (trial)
        {
            install.Write(state with { Pending = false });
            install.Prune();
            confirmed?.Invoke(install.DirectoryOf(running!));
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(_loop, Work).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
