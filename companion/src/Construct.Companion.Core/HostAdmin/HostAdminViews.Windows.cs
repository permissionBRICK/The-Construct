using System.Text.Json.Nodes;
using Construct.Companion.Core.State;
using static Construct.Companion.Core.HostAdmin.HostAdminProtocol;
namespace Construct.Companion.Core.HostAdmin;
public static partial class HostAdminViews
{
    public static JsonObject Windows(JsonNode? input)
    {
        var keys = Map(input?["keys"], k =>
        {
            var row = Strings(k as JsonObject ?? [], "id", "product", "edition", "kind", "partialKey", "notes");
            row["budget"] = Number(k?["budget"]); row["used"] = Number(k?["used"]) ?? 0; return row;
        });
        var guests = Map(input?["guests"], g =>
        {
            var row = Strings(g as JsonObject ?? [], "vmName", "incarnation", "product", "edition", "stage", "activation", "partialKey", "keyId", "error");
            foreach (var key in new[] { "released", "guestReported", "installEjected", "auxiliaryEjected", "kms" }) row[key] = StateJson.Boolean(g?[key]) == true;
            row["status"] = Text(g?["stage"]) + ", " + Text(g?["activation"]) + (Text(g?["partialKey"]).Length > 0 ? " (…" + Text(g?["partialKey"]) + ")" : "") + (StateJson.Boolean(g?["guestReported"]) == true ? " · guest-reported" : "");
            return row;
        });
        return new() { ["keys"] = keys, ["guests"] = guests };
    }
}
