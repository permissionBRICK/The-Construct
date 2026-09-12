using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.State;
namespace Construct.Companion.Core.Desktop;

// Folds the active instance's outbound messages into the tray's view of it.
public sealed class DesktopSnapshot(IClock clock)
{
    private DateTimeOffset unknownSince = clock.UtcNow;
    public TrayState State { get; private set; } = new();
    public IReadOnlyList<TrayForward> Forwards { get; private set; } = [];
    public string? AdminHost { get; private set; }
    private readonly Dictionary<string, bool> online = new(StringComparer.Ordinal);
    // Every registered instance reports through its own state messages; the tooltip counts them.
    public void SetInstances(IEnumerable<string> names)
    {
        var keep = names.ToHashSet(StringComparer.Ordinal);
        foreach (var name in keep) online.TryAdd(name, false);
        foreach (var name in online.Keys.Where(k => !keep.Contains(k)).ToArray()) { online.Remove(name); capturing.Remove(name); }
    }
    public void ApplyOnline(string name, bool value) { if (online.ContainsKey(name)) online[name] = value; }
    private readonly Dictionary<string, bool> capturing = new(StringComparer.Ordinal);
    public void ApplyMic(string name, bool active) { if (online.ContainsKey(name)) capturing[name] = active; }
    public void Select(string? name, bool scriptsFound)
    { State = new(name, scriptsFound); Forwards = []; AdminHost = null; unknownSince = clock.UtcNow; }
    public TrayState Current => State with { UnknownFor = clock.UtcNow - unknownSince, ForwardCount = Forwards.Count, OnlineCount = online.Count(o => o.Value), InstanceCount = online.Count, MicActive = capturing.Any(c => c.Value) };
    private void ApplyOffer(JsonObject? offer)
    { State = State with { HostAdmin = offer is not null }; AdminHost = offer?["url"] is {} url ? RemoteHost.HostSlug(StateJson.String(url)) : null; }
    public void Apply(JsonElement message)
    {
        if (JsonNode.Parse(message.GetRawText()) is not JsonObject m) return;
        switch (StateJson.Text(m["type"]))
        {
            case "state":
                var data = m["state"] as JsonObject ?? m;
                var vm = StateJson.Text(data["vmState"]) ?? "unknown";
                if (vm != "unknown" || State.VmState != "unknown") unknownSince = clock.UtcNow;
                if (StateJson.Text(data["instance"]) is { } owner) ApplyOnline(owner, StateJson.Truthy(data["online"]));
                State = State with { Online = StateJson.Truthy(data["online"]), VmState = vm,
                    ProbeError = StateJson.Truthy(data["probeError"]), UpdateAvailable = StateJson.Truthy(data["constructUpdate"]?["available"]) || StateJson.Truthy(data["update"]?["available"]),
                    ProvisionStale = StateJson.Truthy(data["provisionStale"]) };
                if (data.ContainsKey("hostAdminOffer")) ApplyOffer(data["hostAdminOffer"] as JsonObject);
                if (data["forwards"] is JsonObject forwards) Apply(JsonSerializer.SerializeToElement(new { type = "forwards", forwards }));
                break;
            case "audio":
                State = State with { Mic = StateJson.Truthy(m["enabled"]) };
                if (StateJson.Text(m["instance"]) is { } audioOwner) ApplyMic(audioOwner, StateJson.Truthy(m["capturing"]));
                break;
            case "forwards":
                var rows = m["forwards"]?["items"] as JsonArray ?? m["items"] as JsonArray ?? [];
                Forwards = rows.OfType<JsonObject>().Select(f => new TrayForward(StateJson.String(f["id"]), StateJson.Text(f["label"]) ?? StateJson.String(f["id"]), StateJson.String(f["url"]))).ToArray(); break;
            case "hostAdminOffer":
                ApplyOffer(m["offer"] as JsonObject); break;
            case "lifecycle": State = State with { Busy = StateJson.Truthy(m["busy"]) }; break;
            case "lifecyclePrepared": State = State with { Busy = false }; break;
        }
    }
}
