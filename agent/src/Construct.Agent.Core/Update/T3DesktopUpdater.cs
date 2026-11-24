using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Agent.Device;
using Construct.Agent.Events;

namespace Construct.Agent.Update;

/// <summary>Installed record matching Construct's canonical <c>installed.json</c> schema.</summary>
public sealed record T3DesktopInstallRecord(
    string T3Version,
    string Channel,
    string BuildHash,
    string PatchHash,
    string InstalledAt,
    string SourceInstance);

/// <summary>Validated release manifest from <c>permissionBRICK/construct-t3-builds</c>.</summary>
public sealed record T3DesktopRelease(
    string Version,
    string Channel,
    string BuildHash,
    string PatchHash,
    string DesktopVersion,
    Uri InstallerUrl,
    string InstallerSha256,
    long InstallerSize);

/// <summary>Status of Construct's patched T3 Code Desktop on this device for the local API and UI.</summary>
/// <param name="State"><c>unsupported</c>, <c>not-needed</c>, <c>idle</c>, <c>checking</c>, <c>downloading</c>, <c>waiting</c> (waiting for T3 Desktop to close), <c>installing</c>, <c>failed</c>.</param>
public sealed record T3DesktopUpdateView(
    bool Supported,
    bool Installed,
    string? Channel,
    string? Version,
    string? BuildHash,
    string? Available,
    string? AvailableDesktopVersion,
    string State,
    string? Error,
    DateTimeOffset? CheckedAt,
    bool WaitingForClose);

/// <summary>Outcome of one explicit run inside <see cref="UpdateEverything"/>.</summary>
public sealed record T3DesktopRunResult(string State, string Detail);

/// <summary>
/// Installs and updates Construct's patched T3 Code Desktop app on Windows from <c>permissionBRICK/construct-t3-builds</c>:
/// <list type="bullet">
/// <item>Tracks the channel of the user's VMs that opt into patched T3 Code (<c>T3CODE=true</c>, <c>T3CODE_PATCHED=true</c>),
/// or the already-installed record's channel when no engine answers (S20 independence).</item>
/// <item>Fresh install: when T3 Desktop is not yet installed and a VM uses patched T3 Code (for example after a create or
/// provision job finishes), installs it silently.</item>
/// <item>Background auto-update: when automatic updates are enabled, downloads and SHA-256-verifies the new installer. If T3
/// Desktop is closed, installs it silently without launching it; if T3 Desktop is open, keeps the verified installer staged
/// and installs it silently as soon as T3 Desktop closes — never killing an open window in the background.</item>
/// <item>Explicit update (<c>POST /api/t3-desktop/update/install</c> or <c>POST /api/update/everything</c>): downloads or
/// reuses the staged installer, runs it with <c>--updated /S</c>, updates <c>installed.json</c>, and relaunches T3 Desktop
/// if it was running.</item>
/// </list>
/// </summary>
public sealed partial class T3DesktopUpdater : IAsyncDisposable
{
    public const string DefaultRepository = "permissionBRICK/construct-t3-builds";
    public const string InstallerAssetName = "T3Code-Construct-Setup.exe";
    public const string ServerAssetName = "t3code-server-linux-x64.tar.gz";

    private static readonly string[] RequiredNightlyAssets = ["manifest.json", "SHA256SUMS", InstallerAssetName, ServerAssetName];
    private static readonly JsonSerializerOptions RecordJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IT3DesktopPlatform? _platform;
    private readonly IDeviceEngines _engines;
    private readonly DeviceSettings _settings;
    private readonly HttpClient _http;
    private readonly AgentEvents? _events;
    private readonly Action _changed;
    private readonly TimeProvider _time;
    private readonly string _repository;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private readonly object _gate = new();

    private string _state;
    private string? _error;
    private DateTimeOffset? _checkedAt;
    private string? _channel;
    private string _sourceInstance = "agent";
    private T3DesktopRelease? _offer;
    private T3DesktopRelease? _stagedRelease;
    private string? _stagedPath;
    private bool _waitingForClose;
    private string? _autoFailedBuildHash;
    private bool _installing;

    private Task _loop = Task.CompletedTask;
    private Task _eventLoop = Task.CompletedTask;
    private Task _closeWatcher = Task.CompletedTask;
    private Task _explicitWork = Task.CompletedTask;

    public T3DesktopUpdater(
        IT3DesktopPlatform? platform,
        IDeviceEngines engines,
        DeviceSettings settings,
        HttpClient http,
        Action changed,
        TimeProvider time,
        AgentEvents? events = null,
        string repository = DefaultRepository)
    {
        _platform = platform;
        _engines = engines;
        _settings = settings;
        _http = http;
        _changed = changed;
        _time = time;
        _events = events;
        _repository = repository;
        _state = platform is null ? "unsupported" : "idle";
    }

    public bool Supported => _platform is not null;

    /// <summary>True while an installer run or download is actively executing (used so <see cref="AgentUpdater"/> waits before restarting).</summary>
    public bool Installing
    {
        get { lock (_gate) return _installing; }
    }

    /// <summary>Delay before the first automatic channel check after start.</summary>
    public TimeSpan FirstCheck { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How often the background loop checks the T3 channel, matching <see cref="AgentUpdater"/>.</summary>
    public TimeSpan CheckEvery { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How often the close-watcher checks whether T3 Desktop has exited while an update is staged.</summary>
    public TimeSpan ClosePoll { get; init; } = TimeSpan.FromSeconds(10);

    public Task ExplicitWork
    {
        get { lock (_gate) return _explicitWork; }
    }

    public Task CloseWatcherWork
    {
        get { lock (_gate) return _closeWatcher; }
    }

    public T3DesktopUpdateView View()
    {
        if (_platform is null) return new(false, false, null, null, null, null, null, "unsupported", null, null, false);
        var installed = _platform.IsInstalled();
        var record = ReadInstalledRecord();
        lock (_gate)
        {
            return new(
                Supported: true,
                Installed: installed,
                Channel: _channel ?? record?.Channel,
                Version: record?.T3Version,
                BuildHash: record?.BuildHash,
                Available: _offer?.Version,
                AvailableDesktopVersion: _offer?.DesktopVersion,
                State: _state,
                Error: _error,
                CheckedAt: _checkedAt,
                WaitingForClose: _waitingForClose);
        }
    }

    /// <summary>Starts the periodic update loop and (when events are supplied) watches for succeeded VM provision/update jobs.</summary>
    public void Start()
    {
        if (_platform is null) return;
        _loop = LoopAsync();
        if (_events is not null)
        {
            var subscription = _events.Subscribe(0);
            _eventLoop = Task.Run(() => FollowEventsAsync(subscription));
        }
    }

    private async Task LoopAsync()
    {
        var wait = FirstCheck;
        while (!_lifetime.IsCancellationRequested)
        {
            try { await Task.Delay(wait, _time, _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            wait = CheckEvery;
            try
            {
                await CheckAndAutoApplyAsync(allowFreshInstallWithoutAutoUpdate: false, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (AgentFailure) { }
        }
    }

    private async Task FollowEventsAsync(AgentEvents.Subscription subscription)
    {
        using (subscription)
        {
            try
            {
                await foreach (var item in subscription.Live.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                {
                    if (item.Type != "job.ended" || item.Engine is not { } engine || item.Data is not JsonObject data ||
                        Text(data["id"]) is not { } jobId) continue;
                    _ = Task.Run(() => HandleJobEndedAsync(engine, jobId), CancellationToken.None);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }
    }

    private async Task HandleJobEndedAsync(string engine, string jobId)
    {
        if (_lifetime.IsCancellationRequested) return;
        try
        {
            var job = await _engines.GetAsync(engine, "jobs/" + Uri.EscapeDataString(jobId), _lifetime.Token).ConfigureAwait(false);
            if (!string.Equals(Text(job?["state"]), "succeeded", StringComparison.OrdinalIgnoreCase)) return;
            var kind = Text(job?["kind"]);
            if (kind is not ("create-vm" or "provision" or "update-agents")) return;
            await CheckAndAutoApplyAsync(allowFreshInstallWithoutAutoUpdate: true, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (AgentFailure) { }
    }

    /// <summary>Checks whether a newer (or initial) T3 Code Desktop build is available for this PC.</summary>
    public async Task<T3DesktopUpdateView> CheckAsync(CancellationToken ct)
    {
        if (_platform is null) return View();
        var record = ReadInstalledRecord();
        var installed = _platform.IsInstalled();
        var target = await ResolveTargetAsync(record, ct).ConfigureAwait(false);
        if (target is null)
        {
            lock (_gate)
            {
                _channel = null;
                _offer = null;
                _checkedAt = _time.GetUtcNow();
                if (!_installing && !_waitingForClose) { _state = "not-needed"; _error = null; }
            }

            _changed();
            return View();
        }

        lock (_gate)
        {
            _channel = target.Value.Channel;
            _sourceInstance = target.Value.SourceInstance;
            if (!_installing && !_waitingForClose) { _state = "checking"; _error = null; }
        }

        _changed();
        try
        {
            var release = await FetchReleaseAsync(target.Value.Channel, ct).ConfigureAwait(false);
            var needed = NeedsInstall(record, release, installed);
            lock (_gate)
            {
                _checkedAt = _time.GetUtcNow();
                _offer = needed ? release : null;
                if (!needed)
                {
                    _waitingForClose = false;
                    _stagedRelease = null;
                    _stagedPath = null;
                }

                if (!_installing)
                {
                    _state = _waitingForClose ? "waiting" : "idle";
                    _error = null;
                }
            }

            _changed();
            return View();
        }
        catch (AgentFailure failure)
        {
            lock (_gate)
            {
                _checkedAt = _time.GetUtcNow();
                if (!_installing)
                {
                    _state = _waitingForClose ? "waiting" : "failed";
                    _error = failure.Message;
                }
            }

            _changed();
            throw;
        }
    }

    /// <summary>
    /// Starts an explicit install/update immediately (called by <c>POST /api/t3-desktop/update/install</c>).
    /// Closes T3 Desktop if running and relaunches it afterwards.
    /// </summary>
    public T3DesktopUpdateView StartInstall()
    {
        if (_platform is null) throw NotAvailable.Feature("T3 Code Desktop updates");
        lock (_gate)
        {
            if (!_explicitWork.IsCompleted) return View();
            _autoFailedBuildHash = null;
            _error = null;
            _explicitWork = Task.Run(() => RunExplicitAsync(progress: null, _lifetime.Token), CancellationToken.None);
        }

        return View();
    }

    /// <summary>Runs an explicit check + install as part of <see cref="UpdateEverything"/>.</summary>
    public Task<T3DesktopRunResult> RunForUpdateEverythingAsync(Action<string>? progress, CancellationToken ct) =>
        RunExplicitAsync(progress, ct);

    /// <summary>
    /// Background auto-apply: checks for an update, and if automatic updates are on (or this is a fresh install after a
    /// VM provision/create), downloads and installs silently while closed or stages and watches for T3 Desktop to close.
    /// </summary>
    public async Task CheckAndAutoApplyAsync(bool allowFreshInstallWithoutAutoUpdate, CancellationToken ct)
    {
        if (_platform is null) return;
        await CheckAsync(ct).ConfigureAwait(false);
        T3DesktopRelease? offer;
        bool installed;
        string sourceInstance;
        lock (_gate)
        {
            offer = _offer;
            sourceInstance = _sourceInstance;
            installed = _platform.IsInstalled();
            if (offer is null) return;
            if (string.Equals(_autoFailedBuildHash, offer.BuildHash, StringComparison.OrdinalIgnoreCase)) return;
        }

        var auto = _settings.Read().AutoUpdate;
        var hasRecord = ReadInstalledRecord() is not null;
        // Fresh install: allowed on post-provision/create even when AutoUpdate is off (matching Provision-AgentVM.ps1),
        // and on periodic checks when AutoUpdate is on and the user has not uninstalled a previously recorded install.
        var shouldApply = installed
            ? auto
            : allowFreshInstallWithoutAutoUpdate || (auto && !hasRecord);
        if (!shouldApply) return;

        await ApplyBackgroundAsync(offer, sourceInstance, ct).ConfigureAwait(false);
    }

    private async Task ApplyBackgroundAsync(T3DesktopRelease release, string sourceInstance, CancellationToken ct)
    {
        if (_platform is null) return;
        await _installGate.WaitAsync(ct).ConfigureAwait(false);
        var startWatcher = false;
        try
        {
            if (!NeedsInstall(ReadInstalledRecord(), release, _platform.IsInstalled()))
            {
                ClearOffer();
                return;
            }

            SetWorking("downloading");
            var path = await EnsureDownloadedAsync(release, ct).ConfigureAwait(false);

            if (_platform.IsRunning())
            {
                // Never kill an open T3 Desktop window in the background: stage and install as soon as it closes.
                lock (_gate)
                {
                    _installing = false;
                    _stagedRelease = release;
                    _stagedPath = path;
                    _sourceInstance = sourceInstance;
                    _waitingForClose = true;
                    _state = "waiting";
                    _error = null;
                    if (_closeWatcher.IsCompleted)
                    {
                        _closeWatcher = Task.Run(WatchForCloseAsync, CancellationToken.None);
                        startWatcher = true;
                    }
                }

                _changed();
                return;
            }

            SetWorking("installing");
            var exitCode = await _platform.RunInstallerAsync(path, allowCloseRunning: false, ct).ConfigureAwait(false);
            if (exitCode != 0) throw new AgentFailure(502, "t3-desktop-install-failed", $"The T3 Code Desktop installer exited {exitCode}.");
            WriteInstalledRecord(release, sourceInstance);
            ClearOffer();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_gate)
            {
                _installing = false;
                if (_state is "downloading" or "installing") _state = _waitingForClose ? "waiting" : "idle";
            }

            _changed();
        }
        catch (Exception ex) when (ex is AgentFailure or IOException or UnauthorizedAccessException or HttpRequestException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            lock (_gate)
            {
                _installing = false;
                _autoFailedBuildHash = release.BuildHash;
                _state = "failed";
                _error = ex is AgentFailure failure ? failure.Message : "T3 Code Desktop could not be updated.";
            }

            _changed();
        }
        finally
        {
            _installGate.Release();
            _ = startWatcher;
        }
    }

    private async Task WatchForCloseAsync()
    {
        if (_platform is null) return;
        while (!_lifetime.IsCancellationRequested)
        {
            bool waiting;
            lock (_gate) waiting = _waitingForClose && _stagedRelease is not null && _stagedPath is not null;
            if (!waiting) return;

            if (_platform.IsRunning())
            {
                try { await Task.Delay(ClosePoll, _time, _lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try { await _installGate.WaitAsync(_lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            T3DesktopRelease? release = null;
            try
            {
                string? path;
                string sourceInstance;
                lock (_gate)
                {
                    if (!_waitingForClose || _stagedRelease is null || _stagedPath is null) return;
                    release = _stagedRelease;
                    path = _stagedPath;
                    sourceInstance = _sourceInstance;
                }

                if (_platform.IsRunning()) continue;
                if (!NeedsInstall(ReadInstalledRecord(), release, _platform.IsInstalled()))
                {
                    ClearOffer();
                    return;
                }

                if (!await VerifyFileHashAsync(path, release.InstallerSha256, _lifetime.Token).ConfigureAwait(false))
                {
                    SetWorking("downloading");
                    path = await EnsureDownloadedAsync(release, _lifetime.Token).ConfigureAwait(false);
                    if (_platform.IsRunning())
                    {
                        lock (_gate) { _installing = false; _stagedPath = path; _state = "waiting"; }
                        _changed();
                        continue;
                    }
                }

                SetWorking("installing");
                var exitCode = await _platform.RunInstallerAsync(path, allowCloseRunning: false, _lifetime.Token).ConfigureAwait(false);
                if (exitCode != 0) throw new AgentFailure(502, "t3-desktop-install-failed", $"The T3 Code Desktop installer exited {exitCode}.");
                WriteInstalledRecord(release, sourceInstance);
                ClearOffer();
                return;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is AgentFailure or IOException or UnauthorizedAccessException or HttpRequestException
                || (ex is OperationCanceledException && !_lifetime.IsCancellationRequested))
            {
                lock (_gate)
                {
                    _installing = false;
                    _waitingForClose = false;
                    _stagedRelease = null;
                    _stagedPath = null;
                    if (release is not null) _autoFailedBuildHash = release.BuildHash;
                    _state = "failed";
                    _error = ex is AgentFailure failure ? failure.Message : "T3 Code Desktop could not be updated.";
                }

                _changed();
                return;
            }
            finally
            {
                _installGate.Release();
            }
        }
    }

    private async Task<T3DesktopRunResult> RunExplicitAsync(Action<string>? progress, CancellationToken ct)
    {
        if (_platform is null) return new("skipped", "Not available on this device.");
        await _installGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var record = ReadInstalledRecord();
            var alreadyInstalled = _platform.IsInstalled();
            var target = await ResolveTargetAsync(record, ct).ConfigureAwait(false);
            if (target is null)
            {
                lock (_gate)
                {
                    _channel = null;
                    _offer = null;
                    _waitingForClose = false;
                    _stagedRelease = null;
                    _stagedPath = null;
                    _checkedAt = _time.GetUtcNow();
                    _state = "not-needed";
                    _error = null;
                }

                _changed();
                return new("skipped", "Not enabled on any VM.");
            }

            lock (_gate)
            {
                _channel = target.Value.Channel;
                _sourceInstance = target.Value.SourceInstance;
                _state = "checking";
                _error = null;
            }

            _changed();
            progress?.Invoke("Checking for updates.");

            var release = await FetchReleaseAsync(target.Value.Channel, ct).ConfigureAwait(false);
            lock (_gate) _checkedAt = _time.GetUtcNow();
            if (!NeedsInstall(record, release, alreadyInstalled))
            {
                ClearOffer();
                return new("done", "Up to date.");
            }

            lock (_gate) _offer = release;
            SetWorking("downloading");
            progress?.Invoke($"Downloading {release.DesktopVersion}.");
            var path = await EnsureDownloadedAsync(release, ct).ConfigureAwait(false);

            var wasRunning = _platform.IsRunning();
            SetWorking("installing");
            progress?.Invoke(wasRunning ? $"Installing {release.DesktopVersion}; T3 Code restarts." : $"Installing {release.DesktopVersion}.");
            var exitCode = await _platform.RunInstallerAsync(path, allowCloseRunning: true, ct).ConfigureAwait(false);
            if (exitCode != 0) throw new AgentFailure(502, "t3-desktop-install-failed", $"The T3 Code Desktop installer exited {exitCode}.");

            WriteInstalledRecord(release, target.Value.SourceInstance);
            ClearOffer();

            if (wasRunning)
            {
                try { await _platform.LaunchAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }

            return new("done", alreadyInstalled ? $"Updated to {release.DesktopVersion}." : $"Installed {release.DesktopVersion}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_gate)
            {
                _installing = false;
                _state = _waitingForClose ? "waiting" : "idle";
            }

            _changed();
            return new("failed", "Cancelled.");
        }
        catch (Exception ex) when (ex is AgentFailure or IOException or UnauthorizedAccessException or HttpRequestException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            var message = ex is AgentFailure failure ? failure.Message : "T3 Code Desktop could not be updated.";
            lock (_gate)
            {
                _installing = false;
                _state = "failed";
                _error = message;
            }

            _changed();
            return new("failed", message);
        }
        finally
        {
            _installGate.Release();
        }
    }

    private void SetWorking(string state)
    {
        lock (_gate)
        {
            _installing = true;
            _state = state;
            _error = null;
        }

        _changed();
    }

    private void ClearOffer()
    {
        lock (_gate)
        {
            _installing = false;
            _offer = null;
            _waitingForClose = false;
            _stagedRelease = null;
            _stagedPath = null;
            _autoFailedBuildHash = null;
            _state = "idle";
            _error = null;
        }

        _changed();
    }

    // ---- target channel & installed record ----

    private async Task<(string Channel, string SourceInstance)?> ResolveTargetAsync(T3DesktopInstallRecord? record, CancellationToken ct)
    {
        var vmTargets = new List<(string Vm, string Channel)>();
        foreach (var engine in _engines.List())
        {
            if (engine.EngineId is null) continue;
            try
            {
                var who = await _engines.GetAsync(engine.Id, "whoami", ct).ConfigureAwait(false);
                var user = Text(who?["name"]);
                if (string.IsNullOrWhiteSpace(user)) continue;
                if (await _engines.GetAsync(engine.Id, "vms", ct).ConfigureAwait(false) is not JsonArray vms) continue;
                foreach (var item in vms)
                {
                    if (item is not JsonObject vm) continue;
                    var name = Text(vm["name"]);
                    var owner = Text(vm["owner"]);
                    var kind = Text(vm["kind"]);
                    var deleting = vm["deleting"] is JsonValue d && d.TryGetValue<bool>(out var del) && del;
                    if (string.IsNullOrWhiteSpace(name) || deleting || (kind is not null && !string.Equals(kind, "primary", StringComparison.OrdinalIgnoreCase)) ||
                        !string.Equals(owner, user, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var desired = await _engines.GetAsync(engine.Id, $"vms/{Uri.EscapeDataString(name)}/desired", ct).ConfigureAwait(false);
                        var settings = desired?["desired"]?["settings"] as JsonObject;
                        if (settings is null) continue;
                        if (!IsTrue(settings["t3Code"]) || !IsTrue(settings["t3CodePatched"])) continue;
                        var channel = string.Equals(Text(settings["t3CodeChannel"]), "nightly", StringComparison.OrdinalIgnoreCase) ? "nightly" : "stable";
                        vmTargets.Add((name, channel));
                    }
                    catch (AgentFailure) { }
                }
            }
            catch (AgentFailure) { }
        }

        if (vmTargets.Count > 0)
        {
            if (record?.Channel is "stable" or "nightly")
            {
                var matching = vmTargets.FirstOrDefault(t => string.Equals(t.Channel, record.Channel, StringComparison.OrdinalIgnoreCase));
                if (matching.Vm is not null) return (matching.Channel, matching.Vm);
            }

            return (vmTargets[0].Channel, vmTargets[0].Vm);
        }

        if (record is not null && record.Channel is "stable" or "nightly")
            return (record.Channel, string.IsNullOrWhiteSpace(record.SourceInstance) ? "agent" : record.SourceInstance);

        return null;
    }

    public T3DesktopInstallRecord? ReadInstalledRecord()
    {
        if (_platform is null) return null;
        var path = Path.Combine(_platform.ArtifactDirectory, "installed.json");
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            var node = JsonNode.Parse(text) as JsonObject;
            if (node is null) return null;
            var version = Text(node["t3Version"])?.Trim() ?? "";
            var channel = Text(node["channel"])?.Trim().ToLowerInvariant() ?? "";
            var buildHash = Text(node["buildHash"])?.Trim().ToLowerInvariant() ?? "";
            var patchHash = Text(node["patchHash"])?.Trim().ToLowerInvariant() ?? "";
            var installedAt = Text(node["installedAt"])?.Trim() ?? "";
            var sourceInstance = Text(node["sourceInstance"])?.Trim() ?? "";
            if (version.Length == 0 || channel is not ("stable" or "nightly") || buildHash.Length == 0) return null;
            return new(version, channel, buildHash, patchHash, installedAt, sourceInstance);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void WriteInstalledRecord(T3DesktopRelease release, string sourceInstance)
    {
        if (_platform is null) return;
        Directory.CreateDirectory(_platform.ArtifactDirectory);
        var path = Path.Combine(_platform.ArtifactDirectory, "installed.json");
        var record = new T3DesktopInstallRecord(
            release.Version,
            release.Channel,
            release.BuildHash,
            release.PatchHash,
            _time.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            string.IsNullOrWhiteSpace(sourceInstance) ? "agent" : sourceInstance);
        var temp = path + ".download";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, RecordJson) + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Exact parity with Construct's <c>Get-T3DesktopInstallPlan</c>: compares <c>t3Version</c>, <c>channel</c>, and
    /// <c>patchHash</c> (falling back to <c>buildHash</c>), requiring the application executable to be present on disk.
    /// </summary>
    public static bool NeedsInstall(T3DesktopInstallRecord? installed, T3DesktopRelease release, bool appPresent)
    {
        if (!appPresent || installed is null) return true;
        if (!string.Equals(installed.T3Version, release.Version, StringComparison.Ordinal)) return true;
        if (!string.Equals(installed.Channel, release.Channel, StringComparison.OrdinalIgnoreCase)) return true;
        var sameBuild = installed.PatchHash.Length > 0 && release.PatchHash.Length > 0
            ? string.Equals(installed.PatchHash, release.PatchHash, StringComparison.OrdinalIgnoreCase)
            : string.Equals(installed.BuildHash, release.BuildHash, StringComparison.OrdinalIgnoreCase);
        return !sameBuild;
    }

    // ---- manifest discovery & verified download ----

    public async Task<T3DesktopRelease> FetchReleaseAsync(string channel, CancellationToken ct)
    {
        if (channel is not ("stable" or "nightly")) throw new AgentFailure(400, "invalid-t3-channel", "T3 Code channel must be stable or nightly.");
        Uri manifestUri;
        if (channel == "stable")
        {
            manifestUri = new($"https://github.com/{_repository}/releases/latest/download/manifest.json");
        }
        else
        {
            manifestUri = await FindNightlyManifestUriAsync(ct).ConfigureAwait(false);
        }

        JsonNode? manifest = await GetJsonAsync(manifestUri, "The T3 Code release manifest could not be read.", ct).ConfigureAwait(false);
        return ParseManifest(manifest, channel, _repository);
    }

    private async Task<Uri> FindNightlyManifestUriAsync(CancellationToken ct)
    {
        for (var page = 1; page <= 5; page++)
        {
            var listingUri = new Uri($"https://api.github.com/repos/{_repository}/releases?per_page=100&page={page}");
            var node = await GetJsonAsync(listingUri, "The T3 Code nightly releases could not be listed.", ct).ConfigureAwait(false);
            if (node is not JsonArray releases) throw new AgentFailure(502, "t3-manifest-invalid", "The T3 Code releases listing is invalid.");

            string? newestTag = null;
            string? newestPublished = null;
            foreach (var entry in releases)
            {
                if (entry is not JsonObject release) continue;
                var draft = release["draft"] is JsonValue dv && dv.TryGetValue<bool>(out var isDraft) && isDraft;
                var prerelease = release["prerelease"] is JsonValue pv && pv.TryGetValue<bool>(out var isPre) && isPre;
                var tag = Text(release["tag_name"]);
                var published = Text(release["published_at"]);
                if (draft || !prerelease || tag is null || published is null || !NightlyTagRegex().IsMatch(tag)) continue;
                if (release["assets"] is not JsonArray assets) continue;
                var names = assets.Select(a => Text(a?["name"])).Where(n => n is not null).ToHashSet(StringComparer.Ordinal);
                if (!RequiredNightlyAssets.All(names.Contains)) continue;
                if (newestPublished is null || string.CompareOrdinal(published, newestPublished) > 0)
                {
                    newestTag = tag;
                    newestPublished = published;
                }
            }

            if (newestTag is not null) return new($"https://github.com/{_repository}/releases/download/{newestTag}/manifest.json");
            if (releases.Count < 100) break;
        }

        throw new AgentFailure(502, "t3-manifest-unavailable", "No validated nightly T3 Code build was found.");
    }

    public static T3DesktopRelease ParseManifest(JsonNode? node, string channel, string repository = DefaultRepository)
    {
        static AgentFailure Invalid(string why) => new(502, "t3-manifest-invalid", "The T3 Code release manifest is invalid: " + why + ".");
        if (node is not JsonObject manifest) throw Invalid("not an object");
        if (manifest["formatVersion"] is not JsonValue fv || !fv.TryGetValue<int>(out var formatVersion) || formatVersion != 1)
            throw Invalid("unsupported formatVersion");
        if (!string.Equals(Text(manifest["channel"]), channel, StringComparison.Ordinal))
            throw Invalid("channel mismatch");
        var version = Text(manifest["version"]) ?? "";
        var versionOk = channel == "nightly" ? NightlyVersionRegex().IsMatch(version) : StableVersionRegex().IsMatch(version);
        if (!versionOk) throw Invalid("bad version");
        var buildHash = (Text(manifest["buildHash"]) ?? "").ToLowerInvariant();
        var patchHash = (Text(manifest["patchHash"]) ?? "").ToLowerInvariant();
        if (!Sha256Regex().IsMatch(buildHash) || !Sha256Regex().IsMatch(patchHash)) throw Invalid("bad build/patch hash");
        var expectedTag = $"t3-{version}-{buildHash}";
        if (!string.Equals(Text(manifest["releaseTag"]), expectedTag, StringComparison.Ordinal)) throw Invalid("releaseTag mismatch");
        var desktopVersion = Text(manifest["desktopVersion"]) ?? "";
        if (!desktopVersion.StartsWith(version + "-construct.", StringComparison.Ordinal) || desktopVersion.Length > 100)
            throw Invalid("bad desktopVersion");

        if (manifest["assets"]?[InstallerAssetName] is not JsonObject asset) throw Invalid("missing desktop installer asset");
        var expectedUrl = $"https://github.com/{repository}/releases/download/{expectedTag}/{InstallerAssetName}";
        var url = Text(asset["url"]) ?? "";
        var sha = (Text(asset["sha256"]) ?? "").ToLowerInvariant();
        if (!string.Equals(url, expectedUrl, StringComparison.Ordinal)) throw Invalid("installer URL does not match release");
        if (!Sha256Regex().IsMatch(sha)) throw Invalid("bad installer sha256");
        if (asset["size"] is not JsonValue sv || !sv.TryGetValue<long>(out var size) || size <= 0) throw Invalid("bad installer size");

        if (Text(manifest["sha256"]) is { } topSha && !string.Equals(topSha.Trim(), sha, StringComparison.OrdinalIgnoreCase))
            throw Invalid("top-level sha256 mismatch");
        if (Text(manifest["downloadUrl"]) is { } topUrl && !string.Equals(topUrl, expectedUrl, StringComparison.Ordinal))
            throw Invalid("top-level downloadUrl mismatch");

        return new(version, channel, buildHash, patchHash, desktopVersion, new Uri(expectedUrl), sha, size);
    }

    private async Task<JsonNode?> GetJsonAsync(Uri uri, string failureMessage, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.TryParseAdd("The-Construct");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new AgentFailure(502, "t3-manifest-unavailable", $"{failureMessage} (HTTP {(int)response.StatusCode}).");
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new AgentFailure(502, "t3-manifest-unavailable", failureMessage);
        }
    }

    private async Task<string> EnsureDownloadedAsync(T3DesktopRelease release, CancellationToken ct)
    {
        if (_platform is null) throw NotAvailable.Feature("T3 Code Desktop updates");
        Directory.CreateDirectory(_platform.ArtifactDirectory);
        var destination = Path.Combine(_platform.ArtifactDirectory, InstallerAssetName);
        if (await VerifyFileHashAsync(destination, release.InstallerSha256, ct).ConfigureAwait(false)) return destination;

        var temp = destination + ".download-" + Guid.NewGuid().ToString("n")[..8];
        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, release.InstallerUrl))
            {
                request.Headers.UserAgent.TryParseAdd("The-Construct");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new AgentFailure(502, "t3-download-failed", $"The T3 Code Desktop installer download answered {(int)response.StatusCode}.");
                await using var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write);
                await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            if (!await VerifyFileHashAsync(temp, release.InstallerSha256, ct).ConfigureAwait(false))
                throw new AgentFailure(502, "t3-hash-mismatch", "The downloaded T3 Code Desktop installer does not match the manifest SHA-256.");

            File.Move(temp, destination, overwrite: true);
            return destination;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    private static async Task<bool> VerifyFileHashAsync(string path, string expectedHex, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        try
        {
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual),
                Encoding.ASCII.GetBytes(expectedHex.ToLowerInvariant()));
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool IsTrue(JsonNode? node) =>
        (node is JsonValue b && b.TryGetValue<bool>(out var flag) && flag)
        || string.Equals(Text(node)?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex StableVersionRegex();

    [GeneratedRegex(@"^\d+\.\d+\.\d+-nightly\.\d+\.\d+$")]
    private static partial Regex NightlyVersionRegex();

    [GeneratedRegex(@"^t3-\d+\.\d+\.\d+-nightly\.\d+\.\d+-[a-f0-9]{64}$")]
    private static partial Regex NightlyTagRegex();

    [GeneratedRegex(@"^[a-f0-9]{64}$")]
    private static partial Regex Sha256Regex();

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        Task watcher;
        Task explicitWork;
        lock (_gate) { watcher = _closeWatcher; explicitWork = _explicitWork; }
        try { await Task.WhenAll(_loop, _eventLoop, watcher, explicitWork).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _installGate.Dispose();
        _lifetime.Dispose();
    }
}
