using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Audio;
using Construct.Companion.Core.Forwards;
using Construct.Companion.Core.Probe;
using Construct.Companion.Core.Drivers;
using Construct.Companion.Core.Repatch;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.State;
using Construct.Companion.Host.ConfigSync;
using Construct.Companion.Host.Ipc;
using Construct.Companion.Host.Runtime;
namespace Construct.Companion.Host.Composition;

public sealed class CompanionInstance(JsonObject definition, InstanceStateStore store, ISshTransport ssh)
{
    public JsonObject Definition { get; set; } = definition;
    public string Name => StateJson.String(Definition["name"]);
    public InstanceStateStore Store { get; set; } = store;
    public ISshTransport Ssh { get; set; } = ssh;
    public SemaphoreSlim Serial { get; } = new(1, 1);
    public InstanceRuntime? Runtime { get; set; }
    public ConfigSyncArea? ConfigSync { get; set; }
    public SemaphoreSlim EnrichmentSerial { get; } = new(1, 1);
    public Dictionary<string, (DateTimeOffset At, string? Raw)> UsageCache { get; } = new();
    public string UsagePeriod { get; set; } = "daily";
    public JsonObject? Usage { get; set; }
    public string? UsageRaw { get; set; }
    public JsonObject Enrichment { get; set; } = new();
    public JsonNode? ConfigState { get; set; }
}
public sealed class CompanionInstances(IStateFileSystem files, IpcSettings settings, IInstanceConnections connections,
    IClock clock, IHypervisorState hypervisor, IProcessRunner processes, IRemoteApi remote, ITokenStore tokens,
    IRuntimeProcesses runtimeProcesses, IPortReservations ports, IToastRaiser toasts, IAudioServerFactory audioServers,
    SharedAudioCapture capture, RuntimeMessageBus bus, ConfigSyncFactory config) : IRuntimeRegistry, IAsyncDisposable
{
    
    private readonly ConcurrentDictionary<string, CompanionInstance> entries = new(StringComparer.Ordinal);
    public HostState Host { get; } = new(files);
    public InstanceRegistry Registry
    {
        get { var value = InstanceRegistry.Load(files);
            if (value.Synthesized && Host.ResolveScriptsDirectory(overrideDirectory: settings.Read().ScriptsDir) is null) value.ByName.Clear();
            return value; }
    }
    public string[] Names => Registry.List().Select(i => StateJson.String(i["name"])).ToArray();
    public CompanionInstance Get(string name)
    {
        var registry = Registry;
        if (!registry.ByName.ContainsKey(name)) throw new IpcFailure(404, "instanceNotFound", "Unknown Construct instance.");
        return entries.GetOrAdd(name, _ =>
        {
            var definition = registry.Resolve(name);
            var directory = Host.ResolveScriptsDirectory(StateJson.Text(definition["scriptsDir"]), settings.Read().ScriptsDir);
            var entry = new CompanionInstance(definition, new(files, name, directory), connections.Ssh(definition));
            EnsureConfig(entry); return entry;
        });
    }
    public RemoteHostClient? Remote(CompanionInstance instance)
    {
        var service = instance.Definition["service"] as JsonObject;
        return StateJson.Text(service?["url"]) is { Length: > 0 } url
            ? new(remote, files, tokens, url, StateJson.Text(service?["auth"]) == "token" ? RemoteAuthentication.Token : RemoteAuthentication.Negotiate) : null;
    }
    public Task<IReadOnlyList<RuntimeInstance>> ReadAsync(CancellationToken cancellationToken)
    {
        var preferences = settings.Read();
        IReadOnlyList<RuntimeInstance> result = Registry.List().Select(definition =>
        {
            var name = StateJson.String(definition["name"]);
            var directory = Host.ResolveScriptsDirectory(StateJson.Text(definition["scriptsDir"]), preferences.ScriptsDir);
            var store = new InstanceStateStore(files, name, directory); var form = store.ReadSettings();
            var revision = definition.ToJsonString() + "|" + directory + "|" + preferences.MicDevice;
            return new RuntimeInstance(name, revision, preferences.Forwards.Enabled, preferences.Forwards.HostLabel, preferences.Notifications,
                StateJson.Boolean(form["mic"]) == true, StateJson.Boolean(form["partialStreaming"]) == true, preferences.RepatchDelaySeconds);
        }).ToArray();
        return Task.FromResult(result);
    }
    public IDisposable Watch(Action changed) => files.Watch(Path.Combine(Host.LocalAppData!, "The-Construct"), changed);
    public InstanceRuntime CreateRuntime(RuntimeInstance definition)
    {
        var entry = Get(definition.Name); var current = Registry.Resolve(definition.Name);
        var directory = Host.ResolveScriptsDirectory(StateJson.Text(current["scriptsDir"]), settings.Read().ScriptsDir);
        entry.Definition = current; entry.Store = new(files, definition.Name, directory); entry.Ssh = connections.Ssh(current);
        EnsureConfig(entry);
        var probe = new InstanceProbe(entry, this, hypervisor, processes);
        // Connection construction is asynchronous (remote user credential); lazy transport defers it until start.
        var runtime = new InstanceRuntime(definition, probe, clock,
            changed => new Forwarder(definition.Name, new DeferredForwardTransport(ct => connections.ForwardsAsync(current, entry.Ssh, ct)), runtimeProcesses, ports, clock, changed),
            () => new(definition.Name, entry.Ssh, runtimeProcesses, toasts, clock),
            changed => new(entry.Ssh, runtimeProcesses, audioServers, capture, changed), new RepatchJob(entry.Ssh), bus);
        entry.ConfigSync?.Runtime.StartWatching();
        entry.Runtime = runtime; return runtime;
    }
    private void EnsureConfig(CompanionInstance entry)
    {
        if (entry.ConfigSync is null && Host.ConfigDirectory is { } cfg)
            entry.ConfigSync = config.Create(cfg, Path.Combine(Host.LocalAppData!, "The-Construct", "cache", "config-remotes"), entry.Ssh, StateJson.String(entry.Definition["configBranch"]));
    }
    public async Task<IAsyncDisposable?> AcquireRetargetAsync(string name, CancellationToken ct)
    {
        if (!entries.TryGetValue(name, out var entry)) return new RetargetLease(null);
        if (!await entry.Serial.WaitAsync(0, ct)) return null;
        try
        {
            entry.Runtime = null;
            var current = Registry.Resolve(name);
            if ((!Registry.ByName.ContainsKey(name) || !JsonNode.DeepEquals(current, entry.Definition)) && entry.ConfigSync is { } area)
            { await area.DisposeAsync(); entry.ConfigSync = null; entry.ConfigState = null; entry.UsageCache.Clear(); }
            return new RetargetLease(entry.Serial);
        }
        catch { entry.Serial.Release(); throw; }
    }
    private sealed class RetargetLease(SemaphoreSlim? serial) : IAsyncDisposable
    { public ValueTask DisposeAsync() { serial?.Release(); return ValueTask.CompletedTask; } }
    public async ValueTask DisposeAsync()
    { foreach (var entry in entries.Values) if (entry.ConfigSync is { } area) await area.DisposeAsync(); }
    private sealed class InstanceProbe(CompanionInstance entry, CompanionInstances owner, IHypervisorState hypervisor, IProcessRunner processes) : IRuntimeProbe
    {
        public async Task<JsonObject> ProbeAsync(CancellationToken cancellationToken)
        {
            var response = await entry.Ssh.RunRemoteScriptAsync(GuestScripts.Render("probe"), TimeSpan.FromSeconds(20), cancellationToken);
            var state = response.Code == 0 ? ProbeParser.ToState(ProbeParser.ParseProbe(response.Stdout), StateJson.Text(entry.Definition["vmHost"])) : new JsonObject { ["online"] = false };
            state["online"] = response.Code == 0;
            var vmName = StateJson.String(entry.Definition["vmName"]);
            state["vmState"] = StateJson.Boolean(state["online"]) == true ? "running" : owner.Remote(entry) is { } client
                ? await VmPower.QueryRemoteAsync(client, vmName, cancellationToken) : await VmPower.QueryLocalAsync(hypervisor, processes, vmName, cancellationToken);
            entry.Store.BackfillFromProbe(state);
            return state;
        }
    }
}
internal sealed class DeferredForwardTransport(Func<CancellationToken, Task<IForwardTransport>> create) : IForwardTransport
{
    private IForwardTransport? inner;
    public bool IsRemote => inner?.IsRemote ?? false;
    public async Task<string> CheckCapabilityAsync(CancellationToken ct) { inner ??= await create(ct); return await inner.CheckCapabilityAsync(ct); }
    private IForwardTransport Inner => inner ?? throw new InvalidOperationException("Forward transport has not started.");
    public IRunningProcess SpawnWatch(CancellationToken ct) => Inner.SpawnWatch(ct);
    public Task<JsonObject?> ReadAsync(CancellationToken ct) => Inner.ReadAsync(ct);
    public Task WriteAckAsync(string id, JsonObject document, CancellationToken ct) => Inner.WriteAckAsync(id, document, ct);
    public Task SweepAsync(string sub, string id, CancellationToken ct) => Inner.SweepAsync(sub, id, ct);
    public Task CloseAsync(string id, CancellationToken ct) => Inner.CloseAsync(id, ct);
    public Task ReleaseAsync(CancellationToken ct) => inner?.ReleaseAsync(ct) ?? Task.CompletedTask;
    public IRunningProcess SpawnTunnel(TunnelSpec spec, CancellationToken ct) => Inner.SpawnTunnel(spec, ct);
    public Task<bool> ProbePortAsync(int port, string bindHost, CancellationToken ct) => Inner.ProbePortAsync(port, bindHost, ct);
}
