using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Companion.Core.Runtime;
using static Construct.Companion.Core.Runtime.RuntimeJson;
using static Construct.Companion.Core.Forwards.ForwardProtocol;

namespace Construct.Companion.Core.Forwards;

public static class RemoteForwardList
{
    public static JsonObject? ReadDestination(JsonObject? raw, int vmPort)
    {
        if (raw is null) return null;
        var name = Trim(raw.Str("vmName")); var address = Trim(raw.Str("connectAddress")); var via = Trim(raw.Str("via"));
        if (!IsSafeId(name) || address.Length == 0 || SshArgs.NormalizeConnectAddress(address) is null) return null;
        return new() { ["vmName"] = name, ["via"] = IsSafeId(via) ? via : "", ["connectAddress"] = address.StartsWith('[') && address.EndsWith(']') ? address[1..^1] : address,
            ["connectPort"] = Port(raw["connectPort"]) ?? vmPort, ["verified"] = raw.True("verified") };
    }
    public static JsonObject Read(JsonArray entries)
    {
        var result = new JsonObject { ["requests"] = new JsonArray(), ["acks"] = new JsonArray(), ["host"] = new JsonArray(), ["closes"] = new JsonArray(), ["pending"] = new JsonArray() };
        foreach (var raw in entries.OfType<JsonObject>())
        {
            var id = raw.Str("id"); if (!IsSafeId(id) || Port(raw["vmPort"]) is not int port) continue;
            var target = raw["target"] is null ? "client" : raw.Str("target").ToLowerInvariant();
            var label = Sanitize(raw.Str("label"), 100);
            if (target != "client")
            {
                result.Array("host").Add(new JsonObject { ["id"] = id, ["vmPort"] = port, ["label"] = label, ["target"] = target,
                    ["publicPort"] = Port(raw["publicPort"]), ["url"] = raw["url"] is JsonValue url && url.TryGetValue<string>(out var s) && s.Length > 0 ? s : null }); continue;
            }
            if (raw.Str("status").Equals("closed", StringComparison.OrdinalIgnoreCase) || raw.Str("state").Equals("closed", StringComparison.OrdinalIgnoreCase))
            { result.Array("closes").Add(id); continue; }
            var destination = ReadDestination(raw["destination"] as JsonObject, port);
            var message = Sanitize(raw.Str("message"));
            if (raw["destination"] is not null && destination is null)
            {
                var child = Trim(raw["destination"].Str("vmName")); if (!IsSafeId(child)) continue;
                result.Array("pending").Add(new JsonObject { ["id"] = id, ["vmPort"] = port, ["label"] = label, ["target"] = target, ["child"] = child, ["message"] = message }); continue;
            }
            var req = new JsonObject { ["id"] = id, ["vmPort"] = port, ["label"] = label, ["target"] = target };
            if (destination is not null) req["destination"] = destination;
            result.Array("requests").Add(req);
            var status = raw.Str("status").ToLowerInvariant();
            if (status is not ("open" or "error") || destination is not null && status == "error" && Regex.IsMatch(message, "guest address (unknown|changed)", RegexOptions.IgnoreCase)) continue;
            result.Array("acks").Add(new JsonObject { ["id"] = id, ["status"] = status, ["localPort"] = Port(raw["localPort"]), ["hostLabel"] = ForwardHost.Normalize(raw.Str("hostLabel")), ["message"] = message });
        }
        return result;
    }
}
