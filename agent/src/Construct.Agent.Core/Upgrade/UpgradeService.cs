using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Agent.Device;
using Construct.Agent.Engines;

namespace Construct.Agent.Upgrade;

/// <summary>Where the one-time upgrade stands, for the dialog (11.6).</summary>
/// <param name="Legacy">True when the old client's state is on this PC: the dialog shows the upgrade variant.</param>
/// <param name="Steps">The dialog's steps in order, each <c>todo</c>, <c>running</c>, <c>done</c>, <c>skipped</c> or <c>failed</c>; the engine step <c>restart</c> when Windows must restart to turn on Hyper-V.</param>
/// <param name="Found">What the converter will read (counts and names, never a value).</param>
/// <param name="Notes">What the last run says the user should know, until the user went on from them (<c>POST /api/upgrade/notes/read</c>); only to the account they were written for.</param>
/// <param name="OldHosts">Shared hosts that still run the old stack: this device stays signed in the old way there until their admin updates them.</param>
public sealed record UpgradeView(bool Legacy, string Stage, IReadOnlyList<UpgradeStep> Steps, UpgradeFound? Found, string? StoreEngine, string? LocalEngine,
    IReadOnlyList<UpgradeItem> Items, IReadOnlyList<string> Notes, IReadOnlyList<string> OldWriters, bool Running, string? Error, IReadOnlyList<string> OldHosts);

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
public sealed class UpgradeService
{
    private readonly DeviceAgent _agent;
    private readonly AgentOptions _options;
    private readonly IUpgradePlatform _platform;
    private readonly DeviceSettings _settings;
    private readonly IDeviceEngines _engines;
    private readonly LocalEngineSetup _setup;
    private readonly object _gate = new();
    private Task _work = Task.CompletedTask;
    private string? _running;
    private string? _error;

    /// <param name="setup">Sets up the engine on this PC; the first run uses the same.</param>
    public UpgradeService(DeviceAgent agent, AgentOptions options, IUpgradePlatform platform, DeviceSettings settings, IDeviceEngines engines, LocalEngineSetup setup)
    {
        _agent = agent;
        _options = options;
        _platform = platform;
        _settings = settings;
        _engines = engines;
        _setup = setup;
        Bridge = new BridgeFile(Path.Combine(options.DataDirectory, "bridge.json"));
        // The first-device setup of an upgrade takes the old vault key as K (8.2, 13.1).
        agent.SetupVaultKey = SetupVaultKey;
    }

    public BridgeFile Bridge { get; }

    /// <summary>Tests replace the converter's knobs (a cut in the middle).</summary>
    public Func<string, Task>? BeforeItem { get; set; }

    /// <summary>The package the engine step installs; tests replace it (<see cref="LocalEngineSetup.Installer"/>).</summary>
    public Func<CancellationToken, Task<string>>? EngineInstaller
    {
        get => _setup.Installer;
        set => _setup.Installer = value;
    }

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
        var setup = _setup.View();
        var steps = new List<UpgradeStep>
        {
            bridge.Engine is "done" or "skipped" ? Step("engine", bridge.Engine)
                : setup.State == "restart" ? Step("engine", "restart", setup.Message) : Step("engine", "todo"),
            // Done only once the user confirmed the code was kept: until then the dialog shows it again, newly issued.
            Step("recovery", _agent.IsRegistered && !_agent.RecoveryUnconfirmed ? "done" : "todo"),
            Step("close", bridge.Stage == BridgeState.Converted ? "done" : writers.Count == 0 ? "done" : "todo", writers.Count == 0 ? null : string.Join(", ", writers) + " still open."),
            Step("convert", bridge.Stage == BridgeState.Converted ? "done" : bridge.Items.Any(i => i.State == "failed") ? "failed" : "todo"),
        };
        return new UpgradeView(legacy is not null, bridge.Stage, steps, legacy is null ? null : Found(legacy), bridge.StoreEngine ?? _agent.StoreEngineId, bridge.LocalEngine,
            bridge.Items, !bridge.NotesRead && bridge.NotesAccount is { } owner && owner == _agent.Account ? bridge.Notes : [], writers, _running is not null, error,
            bridge.OldHosts);

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
            localEngine = await _setup.RunAsync(legacy.BootstrapKey(), ct).ConfigureAwait(false);

        // The shared hosts are added for sign-in now; the converter enrols this device there.
        string? firstHost = null;
        foreach (var host in legacy.Hosts().Where(h => h.Pin is not null))
        {
            if (_agent.EngineIdFor(host.Url) is { } known) { firstHost ??= known; continue; }
            var token = host.HasToken ? legacy.HostToken(host) : null;
            var added = await _agent.AddEngineAsync(host.Url, host.Pin!, null, token, ct).ConfigureAwait(false);
            if (token is not null) firstHost ??= added.Id;
            if (token is not null) await MarkOldHostAsync(_agent, Bridge, added.Id, ct).ConfigureAwait(false);
        }

        var store = _agent.StoreEngineId ?? localEngine ?? firstHost
            ?? throw new AgentFailure(409, "no-engine", "No engine to keep the account's store: this PC has no local VM and no shared host with a stored sign-in.");
        Bridge.Update(b => b with { Engine = local ? "done" : "skipped", LocalEngine = localEngine, StoreEngine = store });
    }

    /// <summary>Notes a shared host as still on the old stack, or no longer, as far as it told; the dialog names it before the conversion.</summary>
    internal static async Task MarkOldHostAsync(DeviceAgent agent, BridgeFile bridge, string engineId, CancellationToken ct)
    {
        var old = await agent.RunsOldStackAsync(engineId, ct).ConfigureAwait(false);
        if (old is null || agent.Engines().FirstOrDefault(e => e.Id == engineId) is not { } engine) return;
        bridge.Update(b => b with
        {
            OldHosts = old.Value ? (b.OldHosts.Contains(engine.Name) ? b.OldHosts : [.. b.OldHosts, engine.Name]) : b.OldHosts.Where(h => h != engine.Name).ToArray(),
        });
    }

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
        if (!_agent.IsRegistered || _agent.RecoveryUnconfirmed) throw new AgentFailure(409, "not-registered", "Set up the recovery code first.");
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

    /// <summary>The user went on from the notes the conversion left; they are not shown again.</summary>
    public UpgradeView ReadNotes()
    {
        if (Bridge.Read().Stage != BridgeState.Converted) throw new AgentFailure(409, "not-converted", "The conversion has not finished.");
        Bridge.Update(b => b with { NotesRead = true });
        _agent.Events.Publish("upgrade");
        return View();
    }

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
                catch (AgentFailure failure) when (failure.Code == "restart-needed")
                {
                    // Not an error: the engine step says so, with Restart now (LocalEngineSetup).
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
}
