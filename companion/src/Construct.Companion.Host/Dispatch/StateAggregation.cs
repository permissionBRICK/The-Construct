using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Lifecycle;
using Construct.Companion.Core.State;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Ipc;
using Construct.Companion.Host.Runtime;
namespace Construct.Companion.Host.Dispatch;

public sealed class StateAggregation(CompanionInstances instances, RuntimeMessageBus bus, IpcEvents events, IStateFileSystem files)
{
    public JsonObject State(string name, JsonObject? probe = null)
    {
        var entry = instances.Get(name);
        var cached = bus.Snapshot(name);
        probe ??= cached.TryGetValue("state", out var state) ? JsonNode.Parse(state.GetRawText())!.AsObject() : new() { ["online"] = false, ["vmState"] = "unknown" };
        var data = (probe["state"] as JsonObject ?? probe).DeepClone().AsObject(); data.Remove("type");
        data["instance"] = name; data["backend"] = entry.Definition["backend"]?.DeepClone(); data["connected"] = false; data["connectedInstance"] = null;
        data["canConvertHost"] = false;
        var pending = StateJson.ReadObject(files, HostConversion.PendingPath(instances.Host.LocalAppData));
        var resultPath = StateJson.Text(pending?["resultPath"]);
        data["hostConversionStatus"] = HostConversion.PendingStatus(name, pending, resultPath is null ? null : StateJson.ReadObject(files, resultPath));
        var names = instances.Names;
        if (names.Length > 1) data["instances"] = JsonSerializer.SerializeToNode(names); else data.Remove("instances");
        if (entry.Definition["service"]?["url"] is JsonValue url && Uri.TryCreate(StateJson.Text(url), UriKind.Absolute, out var uri)) data["serviceHost"] = uri.Host;
        data["usagePeriod"] = entry.UsagePeriod;
        if (entry.Usage is not null) data["usage"] = entry.Usage.DeepClone();
        data["registerOffer"] = null; data["removeOffer"] = null;
        if (entry.ConfigState is not null) data["configSync"] = entry.ConfigState.DeepClone();
        var selected = entry.Store.ReadSelectedProjects().Select(StateJson.String).ToHashSet(StringComparer.Ordinal);
        if (entry.Store.HasPersistedSelection()) data["projects"] = new JsonArray(instances.Host.ListProjectProfiles(instances.Host.ConfigDirectory ?? entry.Store.ScriptsDirectory).Select(p => (JsonNode)new JsonObject { ["name"] = p, ["selected"] = selected.Contains(p) }).ToArray());
        foreach (var kind in new[] { "forwards", "children", "idlePolicy", "hostAdminOffer" })
            data[kind] = cached.TryGetValue(kind, out var message) && message.TryGetProperty(kind == "hostAdminOffer" ? "offer" : kind, out var value) ? JsonNode.Parse(value.GetRawText()) : null;
        return new() { ["type"] = "state", ["state"] = data };
    }
    public Snapshot Snapshot(string name)
    {
        var entry = instances.Get(name); var cached = bus.Snapshot(name);
        JsonElement Value(object value) => JsonSerializer.SerializeToElement(value, IpcJson.Options);
        JsonElement Narrow(string kind) => cached.TryGetValue(kind, out var found) ? found : Value(new JsonObject { ["type"] = kind, [kind == "hostAdminOffer" ? "offer" : kind] = null });
        return new(Value(State(name)), Value(new { type = "settings", instance = name, settings = entry.Store.ReadSettings() }),
            cached.TryGetValue("audio", out var audio) ? audio : Value(new { type = "audio", instance = name, enabled = false, capturing = false }),
            Narrow("forwards"), Narrow("children"), Narrow("idlePolicy"), Narrow("hostAdminOffer"));
    }
    public void PublishSnapshot(string name)
    {
        var snapshot = Snapshot(name);
        foreach (var message in new[] { snapshot.State, snapshot.Settings, snapshot.Audio, snapshot.Forwards, snapshot.Children, snapshot.IdlePolicy, snapshot.HostAdminOffer })
            if (message is { } value) events.Message(name, value);
    }
    public void Publish(string name, object message) => bus.Publish(name, message);
    public void Forward(RuntimeMessage message)
    {
        if (message.Instance is not { } name || !instances.Names.Contains(name)) return;
        events.Message(name, message.Message.GetProperty("type").GetString() == "state" ? (object)State(name, JsonNode.Parse(message.Message.GetRawText())!.AsObject()) : message.Message);
    }
}
