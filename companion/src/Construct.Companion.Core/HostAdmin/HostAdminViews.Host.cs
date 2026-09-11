using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Companion.Core.State;
using static Construct.Companion.Core.HostAdmin.HostAdminProtocol;
namespace Construct.Companion.Core.HostAdmin;
public static partial class HostAdminViews
{
    public static JsonArray Capacity(JsonNode? input)
    {
        var s = input as JsonObject ?? []; var ram = s["ram"] as JsonObject ?? []; var cpu = s["cpu"] as JsonObject ?? [];
        var used = (Number(ram["reservedBytes"]) ?? 0) + (Number(ram["unmanagedBytes"]) ?? 0) + (Number(ram["headroomBytes"]) ?? 0);
        var result = new JsonArray(new JsonObject { ["id"] = "ram", ["label"] = "RAM", ["pct"] = Pct(JsonValue.Create(used), ram["totalBytes"]), ["text"] = $"{Bytes(ram["availableBytes"])} available of {Bytes(ram["totalBytes"])} (reserved {Bytes(ram["reservedBytes"])}, unmanaged {Bytes(ram["unmanagedBytes"])}, headroom {Bytes(ram["headroomBytes"])})" },
            new JsonObject { ["id"] = "cpu", ["label"] = "CPU allocation", ["pct"] = Number(cpu["budget"]).HasValue ? Pct(cpu["active"], cpu["budget"]) : null, ["text"] = (Number(cpu["active"]).HasValue ? Text(cpu["active"]) : "—") + " allocated vCPU" + (Number(cpu["budget"]).HasValue ? " of a " + Text(cpu["budget"]) + " budget" : " on " + (Number(cpu["logical"]).HasValue ? Text(cpu["logical"]) : "—") + " logical CPUs (no budget)") });
        foreach (var v in Array(s["volumes"]).OfType<JsonObject>())
        {
            var unmounted = Regex.IsMatch(Text(v["root"]), @"^\\\\\?\\Volume\{[^}]+\}\\?$", RegexOptions.IgnoreCase);
            if (unmounted && !(Number(v["growthReservedBytes"]) > 0)) continue;
            used = (Number(v["totalBytes"]) ?? 0) - (Number(v["freeBytes"]) ?? 0) + (Number(v["headroomBytes"]) ?? 0);
            result.Add(new JsonObject { ["id"] = "vol:" + Text(v["root"]), ["label"] = unmounted ? "Storage (unmounted volume)" : "Storage " + Text(v["root"]), ["pct"] = Pct(JsonValue.Create(used), v["totalBytes"]), ["text"] = $"{Bytes(v["availableBytes"])} available of {Bytes(v["totalBytes"])} (free {Bytes(v["freeBytes"])}, growth reserved {Bytes(v["growthReservedBytes"])}, headroom {Bytes(v["headroomBytes"])})" });
        }
        return result;
    }
    public static JsonObject Overview(JsonNode? input)
    {
        var s = input as JsonObject ?? []; var version = s["version"] as JsonObject ?? []; var health = s["health"] as JsonObject ?? []; var cap = s["capacity"] as JsonObject ?? []; var maint = s["maintenance"] as JsonObject ?? [];
        var problems = new JsonArray(); var healthView = new JsonObject();
        foreach (var key in new[] { "hypervisor", "database", "media", "inventory" })
        { healthView[key] = Default(health[key], "unknown"); if (Text(health[key]).Length > 0 && Text(health[key]) != (key == "inventory" ? "complete" : "ok")) problems.Add(key + " " + Text(health[key])); }
        var vv = new JsonObject(); foreach (var key in new[] { "commit", "packageVersion", "source" }) vv[key] = Default(version[key], "unknown"); vv["installedAt"] = FormatWhen(version["installedAt"]);
        return new() { ["version"] = vv, ["health"] = healthView, ["problems"] = problems, ["capacityMode"] = Text(s["capacityMode"]).ToLowerInvariant() == "enforce" ? "enforce" : "observe", ["capacity"] = Capacity(cap), ["capacityEpoch"] = new JsonObject { ["epoch"] = Text(cap["epoch"]), ["observedAt"] = FormatWhen(cap["observedAt"]), ["complete"] = StateJson.Boolean(cap["complete"]) != false },
            ["maintenance"] = Text(maint["phase"]) is not ("" or "open") ? new JsonObject { ["phase"] = Text(maint["phase"]), ["since"] = FormatWhen(maint["since"]), ["updateId"] = Text(maint["updateId"]).Length > 0 ? Text(maint["updateId"]) : null } : null,
            ["activeJobs"] = Map(s["activeJobs"], j => { var r = Strings(j as JsonObject ?? [], "id", "kind", "vmName", "owner", "initiator", "phase"); r["created"] = FormatWhen(j?["created"]); return r; }), ["leaseOverdueCount"] = Number(s["leaseOverdueCount"]) ?? 0, ["unmanagedVmCount"] = Number(s["unmanagedVmCount"]) ?? 0 };
    }
    public static JsonObject IsoCatalog(JsonNode? input)
    {
        var c = input as JsonObject ?? []; var src = c["source"] as JsonObject ?? [];
        var source = Strings(src, "path", "url"); source["sha256Configured"] = StateJson.Boolean(src["sha256Configured"]) == true; source["present"] = StateJson.Boolean(src["present"]) == true; source["size"] = Bytes(src["sizeBytes"]);
        JsonObject? current = null; if (c["current"] is JsonObject cur) { current = Strings(cur, "fileName", "sourceSha256", "bootstrapKeyFingerprint", "hostnameSource"); current["size"] = Bytes(cur["sizeBytes"]); current["builtAt"] = FormatWhen(cur["builtAt"]); }
        return new() { ["mode"] = Default(c["mode"], "unknown"), ["source"] = source, ["current"] = current,
            ["entries"] = Map(c["entries"], e => new() { ["fileName"] = Text(e?["fileName"]), ["size"] = Bytes(e?["sizeBytes"]), ["isCurrent"] = StateJson.Boolean(e?["isCurrent"]) == true, ["builtAt"] = FormatWhen(e?["builtAt"]), ["sidecarReadable"] = StateJson.Boolean(e?["sidecarReadable"]) != false }),
            ["lastBuild"] = c["lastBuild"] is JsonObject last ? new JsonObject { ["at"] = FormatWhen(last["at"]), ["outcome"] = Text(last["outcome"]), ["jobId"] = Text(last["jobId"]) } : null };
    }
    public static JsonObject UpdateActions(JsonNode? status)
    {
        var current = status?["current"] as JsonObject; var s = Text(current?["state"]);
        return new() { ["check"] = true, ["stage"] = current is null || s is not ("checking" or "staged" or "draining" or "handedOff" or "applying" or "interrupted" or "recoveryFailed"), ["apply"] = s == "staged", ["resume"] = s == "interrupted", ["cancel"] = current is not null && s is "checking" or "staged" or "draining", ["resolve"] = s is "interrupted" or "recoveryFailed" };
    }
    public static JsonObject Updates(JsonNode? input)
    {
        var s = input as JsonObject ?? []; var installed = s["installed"] as JsonObject ?? []; var latest = s["latestKnown"] as JsonObject;
        string Short(JsonNode? n) { var text = Text(n); return text[..Math.Min(12, text.Length)]; }
        JsonObject Row(JsonNode? inputRow)
        {
            var row = inputRow as JsonObject ?? []; var result = Strings(row, "packageVersion", "state", "phase", "error", "actor"); result["updateId"] = Text(row["updateId"] ?? row["id"]); result["commit"] = Short(row["commit"]); result["started"] = FormatWhen(row["started"]); result["finished"] = FormatWhen(row["finished"]);
            result["phases"] = Map(row["phases"], p => new() { ["name"] = Text(p?["name"]), ["at"] = FormatWhen(p?["at"]), ["outcome"] = Text(p?["outcome"]), ["error"] = Text(p?["error"]) });
            result["blockingJobs"] = new JsonArray(Array(row["blockingJobs"]).Select(b => (JsonNode?)JsonValue.Create(b is JsonObject ? Text(b["id"] ?? b["jobId"]) : Text(b))).ToArray()); return result;
        }
        return new() { ["supportsAutoApply"] = StateJson.Boolean(s["supportsAutoApply"]) == true, ["updateAvailable"] = Text(latest?["commit"]).Length > 0 && Text(installed["commit"]).Length > 0 && !JsonNode.DeepEquals(latest?["commit"], installed["commit"]),
            ["installed"] = new JsonObject { ["commit"] = Default(installed["commit"], "unknown"), ["packageVersion"] = Default(installed["packageVersion"], "unknown"), ["installedAt"] = FormatWhen(installed["installedAt"]), ["previousCommit"] = Text(installed["previousCommit"]), ["source"] = Default(installed["source"], "unknown") },
            ["current"] = s["current"] is JsonObject current ? Row(current) : null, ["history"] = Map(s["history"], Row),
            ["latestKnown"] = latest is null ? null : new JsonObject { ["commit"] = Short(latest["commit"]), ["packageVersion"] = Text(latest["packageVersion"]), ["publishedAt"] = FormatWhen(latest["publishedAt"]), ["checkedAt"] = FormatWhen(latest["checkedAt"]), ["compatible"] = Copy(latest["compatible"]), ["reasons"] = Copy(latest["reasons"]) },
            ["recoveryRecord"] = s["recoveryRecord"] is JsonObject recovery ? StateJson.Stringify(recovery, true) : "", ["actions"] = UpdateActions(s) };
    }
}
