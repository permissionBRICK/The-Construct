using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.State;
namespace Construct.Companion.Core.Desktop;

public sealed class DesktopSnapshot(IClock clock)
{
    private DateTimeOffset unknownSince=clock.UtcNow;
    public TrayState State { get; private set; } = new();
    public IReadOnlyList<TrayForward> Forwards { get; private set; }=[];
    public string? AdminHost { get; private set; }
    public void Select(string? name,bool scriptsFound)
    { State=new(name,scriptsFound); Forwards=[]; AdminHost=null; unknownSince=clock.UtcNow; }
    public TrayState Current => State with { UnknownFor=clock.UtcNow-unknownSince, ForwardCount=Forwards.Count };
    private void ApplyOffer(JsonObject? offer)
    { State=State with { HostAdmin=offer is not null }; AdminHost=offer?["url"] is {} url ? Construct.Companion.Core.Remote.RemoteHost.HostSlug(StateJson.String(url)) : null; }
    public void Apply(JsonElement message)
    {
        if (JsonNode.Parse(message.GetRawText()) is not JsonObject m) return;
        switch (StateJson.Text(m["type"]))
        {
            case "state":
                var data=m["state"] as JsonObject ?? m;
                var vm=StateJson.Text(data["vmState"]) ?? "unknown";
                if (vm != "unknown" || State.VmState != "unknown") unknownSince=clock.UtcNow;
                State=State with { Online=StateJson.Truthy(data["online"]), VmState=vm,
                    ProbeError=StateJson.Truthy(data["probeError"]), UpdateAvailable=StateJson.Truthy(data["constructUpdate"]?["available"]) || StateJson.Truthy(data["update"]?["available"]) };
                if (data.ContainsKey("hostAdminOffer")) ApplyOffer(data["hostAdminOffer"] as JsonObject);
                if (data["forwards"] is JsonObject forwards) Apply(JsonSerializer.SerializeToElement(new { type="forwards",forwards }));
                break;
            case "audio": State=State with { Mic=StateJson.Truthy(m["enabled"]) }; break;
            case "forwards":
                var rows=m["forwards"]?["items"] as JsonArray ?? m["items"] as JsonArray ?? [];
                Forwards=rows.OfType<JsonObject>().Select(f=>new TrayForward(StateJson.String(f["id"]),StateJson.Text(f["label"]) ?? StateJson.String(f["id"]),StateJson.String(f["url"]))).ToArray(); break;
            case "hostAdminOffer":
                ApplyOffer(m["offer"] as JsonObject); break;
            case "lifecycle": State=State with { Busy=StateJson.Truthy(m["busy"]) }; break;
            case "lifecyclePrepared": State=State with { Busy=false }; break;
        }
    }
}
