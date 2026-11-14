using System.Globalization;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Probe;

namespace Construct.Companion.Core.State;

public static class UsageParser
{
    public static string NormalizeReport(string? report) => report is "daily" or "monthly" or "total" ? report : "daily";
    public static string BuildUsageScript(string? report) => GuestScripts.Render("usage", new Dictionary<string, string> { ["report"] = NormalizeReport(report) });
    public static string FormatTokens(double n)
    {
        if (!double.IsFinite(n) || n <= 0) return "0";
        foreach (var (scale, unit) in new[] { (1e9, "B"), (1e6, "M"), (1e3, "K") }) if (n >= scale) return (n / scale).ToString("F1", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.') + unit;
        return Math.Floor(n + .5).ToString(CultureInfo.InvariantCulture);
    }
    public static string FormatCost(double n) => "$" + (double.IsFinite(n) && n >= 0 ? n : 0).ToString("N2", CultureInfo.InvariantCulture);
    public static (double Tokens, double Cost)? ParseToolUsage(JsonObject? json)
    {
        if (json is null || StateJson.Truthy(json["error"]) || json["totals"] is not JsonObject totals) return null;
        var tokens = Number(totals["totalTokens"]); if (!double.IsFinite(tokens) || tokens <= 0) return null;
        var cost = Number(totals["totalCost"] ?? totals["costUSD"]); if (!double.IsFinite(cost) || cost < 0) cost = 0;
        return (tokens, cost);
    }
    private static double Number(JsonNode? node) => StateJson.CoerceNumber(node);
    public static JsonObject? ParseUsage(JsonObject? combined)
    {
        if (combined?["tools"] is not JsonObject bag) return null;
        var rows = new JsonArray(); double tokens = 0, cost = 0;
        foreach (var (id, label) in new[] { ("claude", "Claude Code"), ("codex", "Codex"), ("opencode", "OpenCode") })
        {
            if (ParseToolUsage(bag[id] as JsonObject) is not {} usage) continue;
            tokens += usage.Tokens; cost += usage.Cost;
            rows.Add(new JsonObject { ["id"] = id, ["label"] = label, ["tokens"] = usage.Tokens, ["tokensText"] = FormatTokens(usage.Tokens), ["costText"] = FormatCost(usage.Cost) });
        }
        return rows.Count == 0 ? null : new JsonObject { ["tools"] = rows, ["totalTokensText"] = FormatTokens(tokens), ["totalCostText"] = FormatCost(cost) };
    }
    public static string BuildExportPayload(string? rawText, string savedAt)
    {
        JsonNode? combined; try { combined = JsonNode.Parse(rawText ?? ""); } catch (System.Text.Json.JsonException) { combined = null; }
        var obj = combined as JsonObject;
        var payload = new JsonObject { ["savedAt"] = savedAt, ["source"] = "construct-control-panel", ["summary"] = ParseUsage(obj), ["report"] = obj?["report"]?.DeepClone(), ["ccusage"] = combined };
        return StateJson.Stringify(payload, true);
    }
}
