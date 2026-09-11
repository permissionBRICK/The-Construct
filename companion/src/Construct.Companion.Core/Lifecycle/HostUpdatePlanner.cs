using System.Text.Json.Nodes;
using Construct.Companion.Core.State;

namespace Construct.Companion.Core.Lifecycle;

// Decisions only. The dispatcher persists pending operations before invoking the API.
public static class HostUpdatePlanner
{
    public static readonly string[] Active = ["checking", "staged", "draining", "handedOff", "applying"];
    public static readonly string[] Terminal = ["succeeded", "cancelled", "stageFailed", "applyFailed", "rolledBack", "rolledBackWithDatabase", "resolvedByAdmin"];
    public static bool ClearPendingOnError(int status) => status is >= 400 and < 500 and not (408 or 429);
    public static bool ShouldCheck(long now, long? lastCheck, bool force = false) => force || lastCheck is null || now - lastCheck >= 15 * 60 * 1000;
    public static JsonObject PlanAdvance(JsonObject? pending, JsonObject? status)
    {
        if (pending is null) return new JsonObject { ["action"] = "none" };
        if (StateJson.Nonempty(pending["updateId"]) is null) return new JsonObject { ["action"] = "stage", ["body"] = new JsonObject { ["releaseTag"] = pending["releaseTag"]?.DeepClone(), ["operationKey"] = pending["operationKey"]?.DeepClone(), ["autoApply"] = true } };
        var current = status?["current"] as JsonObject;
        if (current is null || !JsonNode.DeepEquals(current["updateId"] ?? current["id"], pending["updateId"])) return new JsonObject { ["action"] = "clear", ["error"] = "The host's current update changed. Open update details before retrying." };
        var state = StateJson.Text(current["state"]) ?? "";
        if (Terminal.Contains(state, StringComparer.Ordinal) || state is "interrupted" or "recoveryFailed") return new JsonObject { ["action"] = "clear" };
        if (state == "staged" && StateJson.Boolean(pending["serverAutoApply"]) != true) return new JsonObject { ["action"] = "apply", ["body"] = new JsonObject { ["updateId"] = pending["updateId"]?.DeepClone(), ["operationKey"] = StateJson.String(pending["operationKey"]) + "-apply" } };
        return new JsonObject { ["action"] = "none" };
    }
    public static JsonObject? PlanStart(JsonObject status, JsonObject? checkResult, string operationKey)
    {
        var current = status["current"] as JsonObject; var state = StateJson.Text(current?["state"]) ?? "";
        if (state == "staged") return new JsonObject { ["updateId"] = (current?["updateId"] ?? current?["id"])?.DeepClone(), ["operationKey"] = operationKey, ["serverAutoApply"] = false };
        if (Active.Contains(state, StringComparer.Ordinal) || state is "interrupted" or "recoveryFailed") throw new InvalidOperationException("An update is already active or needs recovery. See update details.");
        var latest = checkResult?["latest"] as JsonObject ?? throw new InvalidOperationException("No host release is available.");
        if (JsonNode.DeepEquals(latest["commit"], (checkResult?["installed"] as JsonObject)?["commit"])) return null;
        if (StateJson.Boolean(latest["compatible"]) == false) throw new InvalidOperationException("Update is incompatible: " + string.Join(", ", (latest["reasons"] as JsonArray ?? []).Select(StateJson.String)));
        return new JsonObject { ["releaseTag"] = latest["releaseTag"]?.DeepClone(), ["operationKey"] = operationKey, ["serverAutoApply"] = StateJson.Boolean(status["supportsAutoApply"]) == true };
    }
}
