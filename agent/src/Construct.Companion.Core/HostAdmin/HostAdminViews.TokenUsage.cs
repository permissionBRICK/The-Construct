using System.Text.Json.Nodes;
using Construct.Companion.Core.State;
using static Construct.Companion.Core.HostAdmin.HostAdminProtocol;
namespace Construct.Companion.Core.HostAdmin;

public static partial class HostAdminViews
{
    public static JsonObject TokenUsage(JsonNode? input)
    {
        JsonObject Amounts(JsonNode? r) => new() { ["tokens"] = UsageParser.FormatTokens(Number(r?["tokens"]) ?? 0), ["cost"] = UsageParser.FormatCost(Number(r?["costUsd"]) ?? 0) };
        JsonObject Details(JsonNode? r)
        {
            var result = Amounts(r); result["lastReported"] = FormatWhen(r?["lastReportedAt"]);
            result["tools"] = string.Join("; ", Array(r?["tools"]).Select(t => $"{Text(t?["tool"])}: {UsageParser.FormatTokens(Number(t?["tokens"]) ?? 0)} tokens, {UsageParser.FormatCost(Number(t?["costUsd"]) ?? 0)}"));
            return result;
        }
        var window = Text(input?["window"]);
        return new() { ["window"] = window is "today" or "month" or "all" ? window : "today",
            ["generatedAt"] = FormatWhen(input?["generatedAt"]), ["totals"] = Amounts(input?["totals"]),
            ["byUser"] = Map(input?["byUser"], u => { var r = Details(u); r["user"] = Text(u?["user"]); r["vms"] = Number(u?["vms"]) ?? 0; return r; }),
            ["byVm"] = Map(input?["byVm"], v => { var r = Details(v); r["vm"] = Text(v?["vm"]); r["user"] = Text(v?["user"]); r["deleted"] = StateJson.Truthy(v?["deleted"]); return r; }) };
    }
}
