using System.Text.Json.Nodes;
using Construct.Companion.Core.Runtime;
using static Construct.Companion.Core.Runtime.RuntimeJson;
using static Construct.Companion.Core.Forwards.ForwardProtocol;

namespace Construct.Companion.Core.Forwards;

public static class ForwardPlanner
{
    private static JsonObject[] Safe(JsonNode input, string key) => input.Array(key).OfType<JsonObject>().Where(e => IsSafeId(e.Str("id"))).ToArray();
    private static Dictionary<string, JsonObject> Map(IEnumerable<JsonObject> rows) => rows.GroupBy(e => e.Str("id")).ToDictionary(g => g.Key, g => g.Last());
    public static JsonArray PlanActions(JsonObject input)
    {
        var actions = new JsonArray();
        if (input.False("owner")) return actions;
        var requests = Safe(input, "requests"); var acks = Safe(input, "acks"); var tunnels = Safe(input, "tunnels");
        var closes = input.Array("closes").Select(Text).Where(IsSafeId).Distinct().ToArray();
        var rm = Map(requests); var am = Map(acks); var tm = Map(tunnels);
        foreach (var id in closes)
        {
            if (tm.ContainsKey(id)) actions.Add(new JsonObject { ["kind"] = "close", ["id"] = id, ["reason"] = "closed" });
            actions.Add(new JsonObject { ["kind"] = "sweep", ["sub"] = "close", ["id"] = id });
        }
        foreach (var t in tunnels)
            if (!closes.Contains(t.Str("id")) && !rm.ContainsKey(t.Str("id"))) actions.Add(new JsonObject { ["kind"] = "close", ["id"] = t.Str("id"), ["reason"] = "gone" });
        foreach (var a in acks)
            if (!rm.ContainsKey(a.Str("id"))) actions.Add(new JsonObject { ["kind"] = "sweep", ["sub"] = "acks", ["id"] = a.Str("id") });
        var busy = tunnels.Where(t => !closes.Contains(t.Str("id")) && rm.ContainsKey(t.Str("id"))).Select(t => Port(t["localPort"])).Where(p => p is not null).ToArray();
        var hostLabel = ForwardHost.Normalize(input.Str("hostLabel"));
        foreach (var request in requests)
        {
            var id = request.Str("id");
            if (closes.Contains(id)) continue;
            tm.TryGetValue(id, out var tunnel); am.TryGetValue(id, out var ack);
            if (tunnel is null)
            {
                var promised = Port(ack?["localPort"]);
                var open = new JsonObject { ["kind"] = "open", ["id"] = id, ["vmPort"] = request["vmPort"]?.DeepClone(),
                    ["taken"] = new JsonArray(busy.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()) };
                if (request.ContainsKey("label")) open["label"] = request["label"]?.DeepClone();
                open[promised is not null && !input.True("reopenAcked") ? "requirePort" : "preferPort"] = promised;
                if (request["destination"] is not null) open["destination"] = request["destination"]!.DeepClone();
                actions.Add(open); continue;
            }
            var localPort = Port(tunnel["localPort"]);
            if (tunnel.Str("state") == "failed")
            {
                var message = Sanitize(tunnel.Str("message"));
                if (message.Length == 0) message = "the tunnel to the VM could not be opened";
                if (ack is null || ack.Str("status") != "error" || ack.Str("message") != message)
                    actions.Add(new JsonObject { ["kind"] = "error", ["id"] = id, ["message"] = message, ["localPort"] = localPort });
                continue;
            }
            if (tunnel.Str("state") != "up" || localPort is null) continue;
            if (tunnel.True("acked") && ack.Str("status") == "open" && Port(ack?["localPort"]) != localPort)
            { actions.Add(new JsonObject { ["kind"] = "adopt", ["id"] = id, ["localPort"] = Port(ack?["localPort"]) }); continue; }
            if (ack is null || ack.Str("status") != "open" || Port(ack["localPort"]) != localPort || ForwardHost.Normalize(ack.Str("hostLabel")) != hostLabel)
                actions.Add(new JsonObject { ["kind"] = "ack", ["id"] = id, ["localPort"] = localPort, ["hostLabel"] = hostLabel });
        }
        return actions;
    }
    public static JsonObject PlanLifecycle(JsonObject input)
    {
        var armed = input.Str("armed"); var state = Trim(input.Str("vmState")).ToLowerInvariant();
        if (input.False("enabled")) return Decision(armed.Length > 0 ? "stop" : "none", "disabled");
        if (input.True("online")) return armed.Length > 0 && armed == input.Str("name") ? Decision("none", "armed") : Decision("start", "reachable");
        return Decision(armed.Length > 0 ? "stop" : "none", state is "off" or "saved" or "absent" ? state : "unreachable");
    }
    public static JsonObject PlanStartOutcome(JsonObject input)
    {
        var outcome = input.Str("outcome");
        if (input.False("current")) return Decision("none", "superseded");
        return outcome switch { "unanswered" => Decision("retry", outcome), "stood-down" => Decision("none", outcome), _ => Decision("keep", outcome.Length > 0 ? outcome : "started") };
    }
    private static JsonObject Decision(string action, string reason) => new() { ["action"] = action, ["reason"] = reason };
    public static JsonObject ToSnapshot(JsonObject input)
    {
        var items = new List<JsonObject>(); var am = Map(Safe(input, "acks")); var tm = Map(Safe(input, "tunnels"));
        var closes = input.Array("closes").Select(Text).ToHashSet();
        foreach (var req in Safe(input, "requests"))
        {
            var id = req.Str("id"); if (closes.Contains(id)) continue;
            am.TryGetValue(id, out var ack); tm.TryGetValue(id, out var tunnel);
            var item = Item(req, "client", true);
            if (req["destination"] is JsonObject dest) item["child"] = dest.Str("vmName");
            if (ack.Str("status") == "error") { item["status"] = "error"; item["message"] = ack.Str("message"); }
            else if (ack.Str("status") == "open" && Port(ack?["localPort"]) is int ap) Open(item, ap, ack.Str("hostLabel"));
            else if (tunnel.Str("state") == "up" && Port(tunnel?["localPort"]) is int tp) Open(item, tp, input.Str("hostLabel"));
            items.Add(item);
        }
        foreach (var record in Safe(input, "host"))
        {
            var item = Item(record, "host", false); var url = record.Str("url");
            item["status"] = url.Length > 0 ? "open" : "queued"; item["localPort"] = Port(record["publicPort"]); item["url"] = url.Length > 0 ? url : null; items.Add(item);
        }
        foreach (var record in Safe(input, "pending"))
        {
            if (closes.Contains(record.Str("id"))) continue;
            var item = Item(record, "client", true); item["status"] = "error"; item["child"] = record.Str("child");
            item["message"] = record.Str("message") is { Length: > 0 } message ? message : "guest address unknown yet"; items.Add(item);
        }
        return new() { ["mode"] = input.Str("mode") == "remote" ? "remote" : "local", ["owner"] = !input.False("owner"),
            ["items"] = List(items.OrderBy(i => Port(i["vmPort"]) ?? 0).ThenBy(i => i.Str("id"), StringComparer.Ordinal)) };
    }
    private static JsonObject Item(JsonObject r, string target, bool owned) => new() { ["id"] = r.Str("id"), ["vmPort"] = r["vmPort"]?.DeepClone(),
        ["label"] = Sanitize(r.Str("label"), 100), ["target"] = target, ["status"] = "queued", ["localPort"] = null, ["url"] = null, ["message"] = "", ["owned"] = owned };
    private static void Open(JsonObject item, int port, string host)
    { var h = ForwardHost.ForUrl(host); item["status"] = "open"; item["localPort"] = port; item["url"] = $"http://{(h.Length == 0 ? "localhost" : h)}:{port}/"; }
}
