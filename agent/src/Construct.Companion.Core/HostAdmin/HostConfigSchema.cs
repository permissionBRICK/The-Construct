using System.Text.Json.Nodes;
using Construct.Companion.Core.State;

namespace Construct.Companion.Core.HostAdmin;

public static class HostConfigSchema
{
    private static readonly JsonArray Schema = Read();
    private static JsonArray Read()
    {
        using var stream = typeof(HostConfigSchema).Assembly.GetManifestResourceStream("HostAdmin.ConfigSchema.json")!;
        return JsonNode.Parse(stream)!.AsArray();
    }
    public static JsonArray Sections => Schema.DeepClone().AsArray();
    public static JsonArray Fields(string key, JsonObject value, JsonNode? capabilities = null)
    {
        var section = Schema.OfType<JsonObject>().Single(s => HostAdminProtocol.Text(s["key"]) == key);
        return new JsonArray(section["fields"]!.AsArray().OfType<JsonObject>().Select(field =>
        {
            var view = field.DeepClone().AsObject();
            var name = HostAdminProtocol.Text(field["key"]);
            view["value"] = (value.ContainsKey(name) ? value[name] : field["default"])?.DeepClone();
            view["visible"] = field["platform"] is null || HostAdminProtocol.Text(field["platform"]) == HostAdminProtocol.Text(capabilities?["backend"]);
            var unavailable = HostAdminProtocol.Text(field["requires"]) == "nested" && StateJson.Boolean(capabilities?["nested"]?["available"]) == false;
            view["disabled"] = unavailable;
            view["note"] = unavailable ? "Nested virtualization is unavailable on this host." : "";
            return (JsonNode)view;
        }).ToArray());
    }
}
