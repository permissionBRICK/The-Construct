using System.Text.Json.Nodes;
using System.Threading.Channels;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Runtime;
using static Construct.Companion.Core.Runtime.RuntimeJson;

namespace Construct.Companion.Core.Forwards;

// All table/spool mutations run under serial. Process callbacks only invalidate the view.
public sealed class Forwarder(string instance, IForwardTransport transport, IRuntimeProcesses processes,
    IPortReservations ports, IClock clock, Action<JsonObject>? changed = null, string hostLabel = "") : IAsyncDisposable
{
    private readonly SemaphoreSlim serial = new(1);
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<bool> invalidated = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<string, Tunnel> tunnels = [];
    private readonly Dictionary<string, DateTimeOffset> retryAfter = [];
    private JsonObject view = new() { ["owner"] = transport.IsRemote, ["requests"] = new JsonArray(), ["acks"] = new JsonArray(), ["closes"] = new JsonArray() };
    private JsonObject snapshot = new() { ["mode"] = transport.IsRemote ? "remote" : "local", ["owner"] = transport.IsRemote, ["items"] = new JsonArray() };
    private string label = ForwardHost.Normalize(hostLabel);
    private bool started;
    private ISupervisedProcess? watcher;
    private Task poll = Task.CompletedTask, events = Task.CompletedTask;
    private string watchBuffer = "";
    public JsonObject Snapshot => Volatile.Read(ref snapshot).Copy();
    public string? StartOutcome { get; private set; }
    public async Task<string> StartAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellationToken);
        await serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (started) return StartOutcome ?? "running";
            var outcome = await transport.CheckCapabilityAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            StartOutcome = outcome;
            if (outcome != "supported") return outcome;
            started = true;
            if (!transport.IsRemote)
                watcher = processes.Start(transport.SpawnWatch, new(TimeSpan.Zero, TimeSpan.FromSeconds(150)), state => { if (state.State == "starting") watchBuffer = ""; }, WatchOutputAsync);
            poll = PollAsync(); events = EventsAsync(); Invalidate();
            return outcome;
        }
        catch (OperationCanceledException) { return "stood-down"; }
        catch { StartOutcome = "unanswered"; return "unanswered"; }
        finally { serial.Release(); }
    }
    private Task WatchOutputAsync(string chunk, CancellationToken token)
    {
        var (lines, rest) = ForwardProtocol.SplitLines(watchBuffer, chunk); watchBuffer = rest;
        if (lines.Contains("CHANGED")) Invalidate();
        return Task.CompletedTask;
    }
    private void Invalidate() => invalidated.Writer.TryWrite(true);
    private async Task PollAsync()
    {
        try { while (true) { await clock.DelayAsync(TimeSpan.FromSeconds(transport.IsRemote ? 10 : 30), stop.Token).ConfigureAwait(false); Invalidate(); } }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    private async Task EventsAsync()
    {
        try
        {
            while (await invalidated.Reader.WaitToReadAsync(stop.Token).ConfigureAwait(false))
            {
                await clock.DelayAsync(TimeSpan.FromMilliseconds(250), stop.Token).ConfigureAwait(false);
                while (invalidated.Reader.TryRead(out _)) { }
                await ReconcileAsync(stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellationToken);
        await serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try { if (started) await ReconcileLockedAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch { /* A failed read/write is retried on the next invalidation. */ }
        finally { serial.Release(); }
    }
    private async Task ReconcileLockedAsync(CancellationToken token)
    {
        var next = await transport.ReadAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        if (next is null) return;
        view = next;
        var leaseReadAt = clock.UtcNow;
        if (next.True("absent") || next.False("owner"))
        {
            foreach (var id in tunnels.Keys.ToArray()) await KillAsync(id).ConfigureAwait(false);
            if (next.True("absent")) { started = false; if (watcher is not null) await watcher.DisposeAsync().ConfigureAwait(false); watcher = null; await stop.CancelAsync().ConfigureAwait(false); }
            Push(); return;
        }
        var requested = view.Array("requests").Select(r => r.Str("id")).ToHashSet();
        foreach (var id in retryAfter.Keys.Where(id => !requested.Contains(id)).ToArray()) retryAfter.Remove(id);
        // A service destination update must replace the old child's address before re-acking.
        foreach (var req in view.Array("requests").OfType<JsonObject>())
            if (tunnels.TryGetValue(req.Str("id"), out var old) && !JsonNode.DeepEquals(old.Destination, req["destination"])) await KillAsync(req.Str("id")).ConfigureAwait(false);
        var suppressed = new HashSet<string>();
        for (var round = 0; round < 3; round++)
        {
            var input = Input(); input["reopenAcked"] = !transport.IsRemote;
            var actions = ForwardPlanner.PlanActions(input);
            foreach (var action in actions.OfType<JsonObject>())
            {
                token.ThrowIfCancellationRequested();
                if (!transport.IsRemote && clock.UtcNow - leaseReadAt >= TimeSpan.FromSeconds(60)) { Invalidate(); Push(); return; }
                var key = action.Str("kind") + ":" + action.Str("id");
                if (suppressed.Contains(key)) continue;
                try { await ApplyAsync(action, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { suppressed.Add(key); }
            }
        }
        Push();
    }
    private JsonObject Input()
    {
        var input = view.Copy(); input["mode"] = transport.IsRemote ? "remote" : "local"; input["hostLabel"] = label;
        input["tunnels"] = List(tunnels.Select(pair => new JsonObject { ["id"] = pair.Key, ["vmPort"] = pair.Value.Spec.VmPort, ["localPort"] = pair.Value.Spec.LocalPort,
            ["state"] = pair.Value.Process.State.State, ["acked"] = pair.Value.Acked, ["message"] = pair.Value.Process.State.Message })); return input;
    }
    private void Push()
    { var next = ForwardPlanner.ToSnapshot(Input()); Volatile.Write(ref snapshot, next); try { changed?.Invoke(next.Copy()); } catch { } }
    private async Task ApplyAsync(JsonObject action, CancellationToken token)
    {
        var id = action.Str("id"); var kind = action.Str("kind");
        if (kind == "open") { await OpenAsync(action, token).ConfigureAwait(false); return; }
        if (kind is "close" or "adopt") { await KillAsync(id).ConfigureAwait(false); return; }
        if (kind == "sweep")
        {
            var sub = action.Str("sub"); await transport.SweepAsync(sub, id, token).ConfigureAwait(false);
            if (sub == "close") view["closes"] = List(view.Array("closes").Where(c => Text(c) != id));
            if (sub == "acks") view["acks"] = List(view.Array("acks").Where(a => a.Str("id") != id)); return;
        }
        if (kind is "ack" or "error")
        {
            var ack = action.Copy(); ack["status"] = kind == "error" ? "error" : "open";
            await WriteAckAsync(id, ack, token).ConfigureAwait(false);
        }
    }
    private async Task WriteAckAsync(string id, JsonObject ack, CancellationToken token)
    {
        var doc = ForwardProtocol.AckDocument(id, ack); await transport.WriteAckAsync(id, doc, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (tunnels.TryGetValue(id, out var t)) t.Acked = true;
        view["acks"] = List(view.Array("acks").Where(a => a.Str("id") != id)); view.Array("acks").Add(ForwardProtocol.ParseAck(id, doc));
    }
    private async Task OpenAsync(JsonObject action, CancellationToken token)
    {
        var id = action.Str("id"); if (tunnels.ContainsKey(id) || retryAfter.TryGetValue(id, out var due) && clock.UtcNow < due) return;
        var vmPort = Port(action["vmPort"])!.Value; var required = Port(action["requirePort"]);
        var slice = ForwardProtocol.InstancePortSlice(instance);
        var taken = tunnels.Values.Select(t => t.Spec.LocalPort).Concat(action.Array("taken").Select(p => Port(p) ?? 0)).ToHashSet();
        var candidates = required is int rp ? new[] { rp } : ForwardProtocol.PortCandidates(vmPort, Port(action["preferPort"]), taken, slice.Base, slice.Count);
        foreach (var port in candidates)
        {
            if (taken.Contains(port)) continue;
            var reservation = ports.TryReserve(port); if (reservation is null) continue;
            try
            {
                var bind = ForwardHost.BindHostFor(label);
                if (!await transport.ProbePortAsync(port, bind, token).ConfigureAwait(false)) continue;
                token.ThrowIfCancellationRequested(); var dest = action["destination"] as JsonObject;
                var spec = new TunnelSpec(port, vmPort, bind, dest?.Str("connectAddress"), Port(dest?["connectPort"]));
                var process = processes.Start(ct => transport.SpawnTunnel(spec, ct), new(TimeSpan.FromMilliseconds(1200)), _ => Invalidate());
                tunnels[id] = new(spec, process, reservation, dest?.Copy()); reservation = null;
                await process.FirstAttempt.WaitAsync(token).ConfigureAwait(false); return;
            }
            finally { reservation?.Dispose(); }
        }
        if (required is not null) return;
        retryAfter[id] = clock.UtcNow + TimeSpan.FromSeconds(60);
        await WriteAckAsync(id, new() { ["status"] = "error", ["message"] = $"no free port on this PC for VM port {vmPort} (tried {vmPort} and 18800-18815)" }, token).ConfigureAwait(false);
    }
    private async Task KillAsync(string id)
    {
        retryAfter.Remove(id);
        if (!tunnels.Remove(id, out var tunnel)) return;
        try { await tunnel.Process.DisposeAsync().ConfigureAwait(false); } finally { tunnel.Reservation.Dispose(); }
    }
    public async Task SetHostLabelAsync(string value, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellationToken);
        await serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var next = ForwardHost.Normalize(value); var before = ForwardHost.BindHostFor(label); label = next;
            if (before != ForwardHost.BindHostFor(next))
                foreach (var t in tunnels.Values)
                {
                    await t.Process.DisposeAsync().ConfigureAwait(false); stop.Token.ThrowIfCancellationRequested();
                    t.Spec = t.Spec with { BindHost = ForwardHost.BindHostFor(next) }; t.Acked = false;
                    t.Process = processes.Start(ct => transport.SpawnTunnel(t.Spec, ct), new(TimeSpan.FromMilliseconds(1200)), _ => Invalidate());
                    await t.Process.FirstAttempt.WaitAsync(stop.Token).ConfigureAwait(false);
                }
            Invalidate();
        }
        finally { serial.Release(); }
    }
    public async Task<bool> CloseAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!ForwardProtocol.IsSafeId(id)) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellationToken);
        await serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!transport.IsRemote && view.False("owner")) return false;
            await KillAsync(id).ConfigureAwait(false); await transport.CloseAsync(id, linked.Token).ConfigureAwait(false); Invalidate(); return true;
        }
        finally { serial.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false); await Task.WhenAll(poll, events).ConfigureAwait(false);
        await serial.WaitAsync().ConfigureAwait(false);
        try
        {
            started = false;
            if (watcher is not null) await watcher.DisposeAsync().ConfigureAwait(false); watcher = null;
            foreach (var id in tunnels.Keys.ToArray()) await KillAsync(id).ConfigureAwait(false);
            try { await transport.ReleaseAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* TTL recovers an unreachable guest. */ }
            view = new() { ["owner"] = transport.IsRemote, ["requests"] = new JsonArray(), ["acks"] = new JsonArray(), ["closes"] = new JsonArray() };
            Push();
        }
        finally { serial.Release(); }
    }
    private sealed class Tunnel(TunnelSpec spec, ISupervisedProcess process, IDisposable reservation, JsonObject? destination)
    {
        public TunnelSpec Spec = spec;
        public ISupervisedProcess Process = process;
        public readonly IDisposable Reservation = reservation;
        public readonly JsonObject? Destination = destination;
        public bool Acked;
    }
}
