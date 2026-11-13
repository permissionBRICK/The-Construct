using System.Text.Json.Nodes;
using Construct.Agent.Events;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Audio;
using Construct.Companion.Core.Forwards;
using Construct.Companion.Core.Notifications;
using Construct.Companion.Core.Runtime;

namespace Construct.Agent.Device;

/// <summary>Where this device dials a VM (7.2): <c>direct</c> on the engine's own machine, else the engine's <c>forwarded</c> endpoint.</summary>
public sealed record VmAddress(string Host, int Port, string Kind);

/// <summary>One of the user's own VMs on one engine, as the device sees it.</summary>
public sealed record VmTarget(string Engine, string Vm, string Alias, string State, VmAddress? Address, bool Ipv4);

/// <summary>A VM on the Connections page: its address, its local forwards and the microphone.</summary>
public sealed record ConnectionView(string Engine, string Vm, string Alias, string State, VmAddress? Address, bool Linked,
    IReadOnlyList<ForwardView> Forwards, AudioView Mic, string? SshConfig);

/// <summary>A forward as the Companion's panel listed it (<see cref="ForwardPlanner.ToPanelForwards"/>): <c>client</c> ones are this device's.</summary>
public sealed record ForwardView(string Id, string Label, string Target, string Status, int? VmPort, int? LocalPort, string? Url, string Message, bool Closable, string? Child)
{
    public static ForwardView From(JsonObject item) => new(item.Str("id"), item.Str("label"), item.Str("target"), item.Str("status"),
        RuntimeJson.Port(item["vmPort"]), RuntimeJson.Port(item["localPort"]), item.Str("url") is { Length: > 0 } url ? url : null, item.Str("message"),
        item.True("closable"), item.Str("child") is { Length: > 0 } child ? child : null);
}

public sealed record AudioView(bool Enabled, bool Capturing, string? Error);

/// <summary>
/// The device's side of every running VM the user owns (5.1): local forwards (the Companion's
/// <see cref="Forwarder"/> with the engine's forward list), <c>construct notify</c> from the VM (its
/// <see cref="Notifier"/>), the microphone (<see cref="AudioSession"/>) and the SSH alias. The VM list is
/// read from each engine every 30 seconds; a VM that stops loses its link. Other users' VMs, and VMs on
/// an engine that does not answer, are left as they are.
/// </summary>
public sealed class VmLinks : IAsyncDisposable
{
    private readonly IDeviceEngines _engines;
    private readonly IDevicePlatform _platform;
    private readonly SshClientConfig _ssh;
    private readonly DeviceSettings _settings;
    private readonly Func<VmTarget, ISshTransport> _sshFor;
    private readonly IRuntimeProcesses _processes;
    private readonly IAudioServerFactory _audioServers;
    private readonly IClock _clock;
    private readonly AgentEvents? _events;
    private readonly IPortReservations _ports = new Runtime.PortReservations();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, Link> _links = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VmTarget> _targets = new(StringComparer.Ordinal);
    private SharedAudioCapture? _capture;
    private Task _loop = Task.CompletedTask;

    public VmLinks(IDeviceEngines engines, IDevicePlatform platform, SshClientConfig ssh, DeviceSettings settings,
        Func<VmTarget, ISshTransport> sshFor, IRuntimeProcesses processes, IAudioServerFactory audioServers, IClock clock, AgentEvents? events)
    {
        _engines = engines;
        _platform = platform;
        _ssh = ssh;
        _settings = settings;
        _sshFor = sshFor;
        _processes = processes;
        _audioServers = audioServers;
        _clock = clock;
        _events = events;
    }

    public static string Key(string engine, string vm) => engine + "/" + vm;

    public void Start() => _loop = LoopAsync();

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await RefreshAsync(_stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception) { /* the next round retries */ }
            try { await _clock.DelayAsync(TimeSpan.FromSeconds(30), _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Reads the user's VMs from every engine and opens or closes links to match.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        var found = new List<(string Engine, IReadOnlyList<VmTarget>? Targets)>();
        foreach (var engine in _engines.List())
        {
            try { found.Add((engine.Id, await DiscoverAsync(engine, ct).ConfigureAwait(false))); }
            catch (AgentFailure) { found.Add((engine.Id, null)); }
        }

        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var known = _engines.List().Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var closing = new List<Link>();
            lock (_targets)
            {
                foreach (var (engine, targets) in found)
                {
                    // An engine that did not answer keeps its links and its list until it answers again.
                    if (targets is null) continue;
                    foreach (var key in _targets.Keys.Where(k => _targets[k].Engine == engine && targets.All(t => Key(t.Engine, t.Vm) != k)).ToArray()) _targets.Remove(key);
                    foreach (var target in targets) _targets[Key(target.Engine, target.Vm)] = target;
                }

                foreach (var key in _targets.Keys.Where(k => !known.Contains(_targets[k].Engine)).ToArray()) _targets.Remove(key);
                foreach (var (key, link) in _links.ToArray())
                {
                    if (_targets.TryGetValue(key, out var target) && target.State == "running" && target.Address == link.Target.Address) continue;
                    _links.Remove(key);
                    closing.Add(link);
                }
            }

            foreach (var link in closing) await link.DisposeAsync().ConfigureAwait(false);
            VmTarget[] opening;
            lock (_targets) opening = _targets.Where(p => p.Value.State == "running" && p.Value.Address is not null && !_links.ContainsKey(p.Key)).Select(p => p.Value).ToArray();
            foreach (var target in opening)
            {
                var link = await OpenLinkAsync(target, ct).ConfigureAwait(false);
                lock (_targets) _links[Key(target.Engine, target.Vm)] = link;
            }
        }
        finally
        {
            _serial.Release();
        }

        Changed();
    }

    /// <summary>The user's own primary VMs on one engine, with the address of each running one.</summary>
    private async Task<IReadOnlyList<VmTarget>> DiscoverAsync(EngineView engine, CancellationToken ct)
    {
        var user = (await _engines.GetAsync(engine.Id, "whoami", ct).ConfigureAwait(false))?["name"]?.GetValue<string>() ?? "";
        var list = await _engines.GetAsync(engine.Id, "vms", ct).ConfigureAwait(false) as JsonArray ?? [];
        var local = IsLoopback(engine.Url);
        var targets = new List<VmTarget>();
        foreach (var vm in list.OfType<JsonObject>())
        {
            var name = Text(vm, "name");
            if (name.Length == 0 || !Text(vm, "owner").Equals(user, StringComparison.OrdinalIgnoreCase) || user.Length == 0) continue;
            if (Text(vm, "kind").Equals("child", StringComparison.OrdinalIgnoreCase) || vm["deleting"]?.GetValue<bool>() == true) continue;
            var state = Text(vm, "state").ToLowerInvariant();
            VmAddress? address = null;
            if (state == "running")
            {
                if (local && _platform.DirectAddresses) address = new(name + ".mshome.net", 22, "direct");
                else
                {
                    try
                    {
                        var endpoint = await _engines.GetAsync(engine.Id, $"vms/{Uri.EscapeDataString(name)}/endpoint", ct).ConfigureAwait(false);
                        if (endpoint?["sshHost"]?.GetValue<string>() is { Length: > 0 } host && endpoint["sshPort"]?.GetValue<int>() is int port)
                            address = new(host, port, "forwarded");
                    }
                    catch (AgentFailure) { /* no endpoint yet: no link this round */ }
                }
            }

            // A host service's forward listens on IPv4 only (SshArgs); a literal IPv6 endpoint is dialled as it is.
            var ipv4 = address is { Kind: "forwarded" } && !(System.Net.IPAddress.TryParse(address.Host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
            targets.Add(new(engine.Id, name, name, state, address, ipv4));
        }

        return targets;
    }

    private async Task<Link> OpenLinkAsync(VmTarget target, CancellationToken ct)
    {
        string? sshConfig = null;
        try { sshConfig = WriteAlias(target).Status; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AgentFailure) { sshConfig = "failed"; }

        if (_ssh.KeyFor(target.Alias) == _ssh.DeviceKeyPath && !File.Exists(_ssh.DeviceKeyPath))
        {
            try { await _ssh.EnsureDeviceKeyAsync("construct-agent", ct).ConfigureAwait(false); }
            catch (AgentFailure) { /* reported by GET /api/device/ssh-key */ }
        }

        var transport = _sshFor(target);
        var link = new Link(target, sshConfig);
        link.Forwarder = new Forwarder(target.Vm, new EngineForwardTransport(_engines, target.Engine, target.Vm, transport), _processes, _ports, _clock, _ => Changed());
        var outcome = await link.Forwarder.StartAsync(ct).ConfigureAwait(false);
        if (outcome != "supported")
        {
            await link.Forwarder.DisposeAsync().ConfigureAwait(false);
            link.Forwarder = null;
        }

        if (_platform.Notifications is { } toasts)
        {
            link.Notifier = new Notifier(target.Vm, transport, _processes, toasts, _clock);
            link.Notifier.Start();
        }

        link.Transport = transport;
        if (_settings.Read().Mic.Contains(Key(target.Engine, target.Vm))) await EnableMicAsync(link, ct).ConfigureAwait(false);
        return link;
    }

    /// <summary>Writes the VM's SSH alias and VS Code's platform entry for it.</summary>
    public ConfigWrite WriteAlias(VmTarget target)
    {
        if (target.Address is not { } address) throw new AgentFailure(409, "vm-not-running", "The VM has no address while it is not running.");
        var write = _ssh.WriteAlias(target.Alias, address.Host, address.Port, target.Ipv4);
        _ssh.WriteVsCodePlatform(target.Alias);
        return write;
    }

    // ---- what the routes read and do ----

    public IReadOnlyList<ConnectionView> Connections()
    {
        lock (_targets)
        {
            return _targets.Values.OrderBy(t => t.Engine, StringComparer.Ordinal).ThenBy(t => t.Vm, StringComparer.Ordinal).Select(t =>
            {
                _links.TryGetValue(Key(t.Engine, t.Vm), out var link);
                var forwards = link?.Forwarder is { } forwarder
                    ? ForwardPlanner.ToPanelForwards(forwarder.Snapshot).Array("items").OfType<JsonObject>().Select(ForwardView.From).ToArray()
                    : [];
                var audio = link?.Audio?.Status;
                return new ConnectionView(t.Engine, t.Vm, t.Alias, t.State, t.Address, link is not null, forwards,
                    new AudioView(audio?.Enabled ?? false, audio?.Capturing ?? false, audio?.Error ?? link?.MicError), link?.SshConfig);
            }).ToArray();
        }
    }

    public VmTarget Target(string engine, string vm)
    {
        lock (_targets) return _targets.TryGetValue(Key(engine, vm), out var target) ? target : throw new AgentFailure(404, "no-vm", "No such VM of yours on that engine (or not seen yet).");
    }

    /// <summary>The SSH transport of a running VM's link, for the open actions and the share.</summary>
    public ISshTransport Transport(string engine, string vm)
    {
        lock (_targets) return _links.TryGetValue(Key(engine, vm), out var link) && link.Transport is { } transport ? transport : throw new AgentFailure(409, "vm-not-running", "The VM is not running.");
    }

    public async Task CloseForwardAsync(string engine, string vm, string id, CancellationToken ct)
    {
        Forwarder forwarder;
        lock (_targets)
            forwarder = _links.TryGetValue(Key(engine, vm), out var link) && link.Forwarder is { } f ? f : throw new AgentFailure(409, "vm-not-running", "The VM has no forwards here now.");
        if (!await forwarder.CloseAsync(id, ct).ConfigureAwait(false)) throw new AgentFailure(404, "no-forward", "No such forward.");
    }

    /// <summary>Switches the microphone passthrough for a VM; the choice is kept for its next start.</summary>
    public async Task<AudioView> SetMicAsync(string engine, string vm, bool on, CancellationToken ct)
    {
        if (_platform.Microphone is null) throw NotAvailable.Feature("The microphone");
        Target(engine, vm);
        var key = Key(engine, vm);
        _settings.Update(s => s with { Mic = on ? [.. s.Mic.Where(k => k != key), key] : [.. s.Mic.Where(k => k != key)] });
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Link? link;
            lock (_targets) _links.TryGetValue(key, out link);
            if (link is null) return new(false, false, on ? "vm-not-running" : null);
            if (on) await EnableMicAsync(link, ct).ConfigureAwait(false);
            else if (link.Audio is { } audio)
            {
                link.Audio = null;
                await audio.DisableAsync(ct).ConfigureAwait(false);
                await audio.DisposeAsync().ConfigureAwait(false);
            }

            var status = link.Audio?.Status;
            return new(status?.Enabled ?? false, status?.Capturing ?? false, status?.Error ?? link.MicError);
        }
        finally
        {
            _serial.Release();
            Changed();
        }
    }

    private async Task EnableMicAsync(Link link, CancellationToken ct)
    {
        if (_platform.Microphone is not { } microphone || link.Transport is null || link.Audio is not null) return;
        _capture ??= new SharedAudioCapture(microphone);
        var audio = new AudioSession(link.Transport, _processes, _audioServers, _capture, _ => Changed());
        link.MicError = null;
        if (await audio.EnableAsync(ct).ConfigureAwait(false)) link.Audio = audio;
        else
        {
            link.MicError = audio.Status.Error;
            await audio.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Changed() => _events?.Publish("connections");

    private static bool IsLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host is "127.0.0.1" or "[::1]" or "localhost");

    private static string Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            Link[] links;
            lock (_targets)
            {
                links = [.. _links.Values];
                _links.Clear();
            }

            foreach (var link in links) await link.DisposeAsync().ConfigureAwait(false);
            if (_capture is not null) await _capture.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    private sealed class Link(VmTarget target, string? sshConfig) : IAsyncDisposable
    {
        public VmTarget Target { get; } = target;
        public string? SshConfig { get; } = sshConfig;
        public ISshTransport? Transport { get; set; }
        public Forwarder? Forwarder { get; set; }
        public Notifier? Notifier { get; set; }
        public AudioSession? Audio { get; set; }
        public string? MicError { get; set; }

        public async ValueTask DisposeAsync()
        {
            if (Audio is not null) await Audio.DisposeAsync().ConfigureAwait(false);
            if (Notifier is not null) await Notifier.DisposeAsync().ConfigureAwait(false);
            if (Forwarder is not null) await Forwarder.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// A VM's forward list on its engine, as the Companion's <c>RemoteForwardTransport</c> reads it: the
/// engine's <c>/vms/{name}/forwards</c> (and those routed through it), acknowledged by this device;
/// tunnels over SSH. The guest's <c>construct expose</c> files the requests with the engine.
/// </summary>
public sealed class EngineForwardTransport(IDeviceEngines engines, string engine, string vm, ISshTransport ssh) : IForwardTransport
{
    private Dictionary<string, string> _targets = [];

    public bool IsRemote => true;

    public Task<string> CheckCapabilityAsync(CancellationToken cancellationToken) => Task.FromResult("supported");

    public IRunningProcess SpawnWatch(CancellationToken cancellationToken) => throw new InvalidOperationException("Engine forwards are polled.");

    public async Task<JsonObject?> ReadAsync(CancellationToken cancellationToken)
    {
        var self = Uri.EscapeDataString(vm);
        JsonArray entries, via;
        try
        {
            if (await engines.GetAsync(engine, $"vms/{self}/forwards", cancellationToken).ConfigureAwait(false) is not JsonArray own) return null;
            entries = own;
            via = await engines.GetAsync(engine, $"vms/{self}/forwards?via={self}", cancellationToken).ConfigureAwait(false) as JsonArray ?? [];
        }
        catch (AgentFailure)
        {
            return null;
        }

        var seen = entries.OfType<JsonObject>().Select(e => e.Str("id")).ToHashSet();
        foreach (var entry in via.OfType<JsonObject>().Where(e => seen.Add(e.Str("id")))) entries.Add(entry.DeepClone());
        var read = RemoteForwardList.Read(entries);
        _targets = read.Array("requests").OfType<JsonObject>().Where(r => r["destination"] is JsonObject)
            .ToDictionary(r => r.Str("id"), r => r["destination"].Str("vmName"));
        foreach (var pending in read.Array("pending").OfType<JsonObject>()) _targets[pending.Str("id")] = pending.Str("child");
        read["owner"] = true;
        return read;
    }

    public async Task WriteAckAsync(string id, JsonObject document, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["status"] = document.Str("status") };
        if (document["localPort"] is not null) body["localPort"] = document["localPort"]!.DeepClone();
        foreach (var key in new[] { "hostLabel", "message" }) if (document.Str(key).Length > 0) body[key] = document.Str(key);
        await engines.PostAsync(engine, Route(id) + "/ack", body, cancellationToken).ConfigureAwait(false);
    }

    public Task SweepAsync(string sub, string id, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CloseAsync(string id, CancellationToken cancellationToken) => engines.SendAsync(engine, HttpMethod.Delete, Route(id), null, cancellationToken);

    public Task ReleaseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string Route(string id)
    {
        if (!ForwardProtocol.IsSafeId(id)) throw new ArgumentException("Unusable forward id.");
        return $"vms/{Uri.EscapeDataString(_targets.GetValueOrDefault(id, vm))}/forwards/{Uri.EscapeDataString(id)}";
    }

    public IRunningProcess SpawnTunnel(TunnelSpec spec, CancellationToken cancellationToken) => ssh.SpawnTunnel(spec, cancellationToken);

    public Task<bool> ProbePortAsync(int port, string bindHost, CancellationToken cancellationToken) => ssh.ProbePortAsync(port, bindHost, cancellationToken);
}
