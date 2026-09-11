using System.Text.Json.Nodes;
using Construct.Companion.Core.State;
namespace Construct.Companion.Core.HostAdmin;
public static partial class HostAdminProtocol
{
    private static double? Num(JsonNode? n) => n is null || Text(n) == "" || !double.IsFinite(StateJson.CoerceNumber(n)) ? null : StateJson.CoerceNumber(n);
    public static bool IsMaintenance(JsonNode? error) => StateJson.Number(error?["status"]) == 503 && Text(error?["code"] ?? error?["body"]?["code"]) is "" or "maintenance";
    public static JsonObject? Maintenance(JsonNode? health, JsonNode? error)
    {
        JsonObject? body = Text(health?["status"]).ToLowerInvariant() == "maintenance" ? health?["maintenance"] as JsonObject ?? [] : IsMaintenance(error) ? error?["body"] as JsonObject ?? [] : null;
        return body is null ? null : new() { ["phase"] = Text(body["phase"]).Length > 0 ? Text(body["phase"]) : "maintenance", ["retryAfterSeconds"] = Num(body["retryAfterSeconds"]), ["updateId"] = Text(body["updateId"]).Length > 0 ? Text(body["updateId"]) : null };
    }
    public static JsonObject Classify(JsonObject input)
    {
        var host = Text(input["host"]); if (host.Length == 0) host = "the host";
        var result = new JsonObject { ["mode"] = "unavailable", ["message"] = "", ["features"] = Features(null), ["maintenance"] = null, ["identity"] = null, ["retryable"] = false };
        JsonObject Done(string mode, string message, bool retryable = false) { result["mode"] = mode; result["message"] = message; result["retryable"] = retryable; return result; }
        var backend = Text(input["backend"]).ToLowerInvariant();
        if (backend.Length > 0 && backend != "hyperv-remote") return Done("local", "This instance runs on this PC's Hyper-V; there is no host service to administer.");
        if (input["healthError"] is JsonObject he)
        {
            if (StateJson.Number(he["status"]) == 404) return Done("old-service", "This host's service predates host administration. Update it on the host (service/host/Install-ConstructHost.ps1); no update can be driven from here.");
            if (IsMaintenance(he)) result["maintenance"] = Maintenance(null, he);
            else return Done("unavailable", $"Cannot reach {host}: {Text(he["message"])}", true);
        }
        else
        {
            result["features"] = Features(input["health"]); result["maintenance"] = Maintenance(input["health"], null);
            if (StateJson.Boolean(result["features"]?["hostAdmin"]) != true) return Done("old-service", "This host's service answers, but does not offer host administration (its apiFeatures lack \"host-admin\"). Update it on the host; no update can be driven from here.");
        }
        var we = input["whoamiError"] as JsonObject;
        if (we is not null)
        {
            if (StateJson.Number(we["status"]) == 401) return Done("sign-in", $"{host} rejected the stored credential. Sign in again with \"The Construct: Add Remote Host\".");
            if (StateJson.Number(we["status"]) == 403) return Done("denied", $"You are not enrolled on {host}; ask its administrator.");
            if (IsMaintenance(we)) result["maintenance"] ??= Maintenance(null, we);
            else return Done("unavailable", $"Cannot reach {host}: {Text(we["message"])}", true);
        }
        var me = input["whoami"] as JsonObject ?? [];
        var identity = new JsonObject { ["name"] = Text(me["name"]), ["role"] = Text(me["role"]).ToLowerInvariant(), ["known"] = StateJson.Boolean(me["known"]) != false, ["enabled"] = StateJson.Boolean(me["enabled"]) != false, ["maxVms"] = Num(me["maxVms"]), ["effective"] = me["effective"] is JsonObject effective ? effective.DeepClone() : null }; result["identity"] = identity;
        if (we is not null && IsMaintenance(we)) return Done("unavailable", $"{host} is updating ({Text(result["maintenance"]?["phase"])}); reconnecting…", true);
        var name = Text(identity["name"]); if (name.Length == 0) name = "This identity";
        if (StateJson.Boolean(identity["known"]) == false || StateJson.Boolean(identity["enabled"]) == false) return Done("denied", $"{name} is not enrolled (or is disabled) on {host}; ask its administrator.");
        return Text(identity["role"]) == "admin" ? Done("admin", "") : Done("user", $"{name} is not an administrator of {host}.");
    }
    public static int? PollInterval(JsonObject state)
    {
        if (state["maintenance"] is not null || StateJson.Truthy(state["updatePending"]) || Text(state["maintenanceTab"]?["current"]?["state"]) is "checking" or "draining" or "handedOff" or "applying") return 5000;
        return Text(state["mode"]) != "admin" ? null : Text(state["activeTab"]) == "vms" ? 10000 : StateJson.Boolean(state["features"]?["updates"]) == true ? 60000 : null;
    }
}
