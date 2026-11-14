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
            var row = Strings(k as JsonObject ?? [], "id", "product", "edition", "kind", "partialKey", "notes", "hostId");
            row["budget"] = Number(k?["budget"]); row["used"] = Number(k?["used"]) ?? 0; return row;
        });
        var guests = Map(input?["guests"], g =>
        {
            var row = Strings(g as JsonObject ?? [], "vmName", "incarnation", "product", "edition", "stage", "activation", "partialKey", "keyId", "error", "hostId");
            foreach (var key in new[] { "released", "guestReported", "installEjected", "auxiliaryEjected", "kms", "evaluation" }) row[key] = StateJson.Boolean(g?[key]) == true;
            row["operation"] = g?["operation"] is JsonObject operation ? Strings(operation, "id", "mode", "stage", "error") : null;
            row["license"] = null;
            if (g?["license"] is JsonObject license)
            {
                var observed = Strings(license, "observedAt", "activationId", "partialKey", "channel", "evaluationEnd");
                foreach (var key in new[] { "status", "reason", "graceMinutes" }) observed[key] = Number(license[key]);
                row["license"] = observed;
            }
            row["status"] = Text(g?["stage"]) + ", " + Text(g?["activation"]) + (Text(g?["partialKey"]).Length > 0 ? " (…" + Text(g?["partialKey"]) + ")" : "") + (StateJson.Boolean(g?["guestReported"]) == true ? " · guest-reported" : "");
            return row;
        });
        var machines = Map(input?["machines"], m => {
            var row = Strings(m as JsonObject ?? [], "id", "keyId", "hostId", "incarnation", "state", "vmName");
            row["hasConfirmationId"] = StateJson.Boolean(m?["hasConfirmationId"]) == true;
            row["reuses"] = Number(m?["reuses"]) ?? 0; return row;
        });
        return new() { ["keys"] = keys, ["guests"] = guests, ["machines"] = machines };
    }
}
