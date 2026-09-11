using System.Globalization;
using System.Text.Json.Nodes;
using Construct.Companion.Core.State;
using static Construct.Companion.Core.HostAdmin.HostAdminProtocol;
namespace Construct.Companion.Core.HostAdmin;

public static partial class HostAdminViews
{
    private static JsonNode? Copy(JsonNode? n) => n?.DeepClone();
    private static JsonArray Array(JsonNode? n) => n as JsonArray ?? [];
    private static string N(double n) => n.ToString(CultureInfo.InvariantCulture);
    private static string Default(JsonNode? n, string fallback) => Text(n) is { Length: > 0 } s ? s : fallback;
    private static double Round(double n) => Math.Floor(n + .5);
    public static string Bytes(JsonNode? value)
    {
        var number = Number(value); if (number is not { } n) return "—";
        if (n >= 1073741824) return (n / 1073741824).ToString(n >= 10737418240 ? "F0" : "F1", CultureInfo.InvariantCulture) + " GiB";
        return n >= 1048576 ? N(Round(n / 1048576)) + " MiB" : n >= 1024 ? N(Round(n / 1024)) + " KiB" : N(n) + " B";
    }
    public static string Duration(double seconds)
    {
        var s = Math.Max(0, Round(seconds)); var d = Math.Floor(s / 86400); var h = Math.Floor(s % 86400 / 3600); var m = Math.Floor(s % 3600 / 60);
        return d > 0 ? $"{N(d)}d {N(h)}h" : h > 0 ? $"{N(h)}h {N(m)}m" : m > 0 ? $"{N(m)}m" : N(s) + "s";
    }
    public static double Pct(JsonNode? part, JsonNode? whole) => Number(part) is { } p && Number(whole) is > 0 and var w ? Math.Clamp(Round(p / w * 100), 0, 100) : 0;
    public static string Lease(JsonNode? lease, DateTimeOffset now)
    {
        if (lease is not JsonObject) return ""; var state = Text(lease["state"]).ToLowerInvariant();
        if (StateJson.Boolean(lease["overdue"]) == true || state == "overdue") return "OVERDUE" + (Text(lease["lastOutcome"]).Length > 0 ? " — " + Text(lease["lastOutcome"]) : "") + " (shutdown due; retried)";
        if (state == "unlimited") return "no expiry"; if (state == "inactive") return "not started";
        if (state == "expired") return "expired" + (lease["expiresAt"] is not null ? " at " + FormatWhen(lease["expiresAt"]) : "");
        if (state == "active" && DateTimeOffset.TryParse(Text(lease["expiresAt"]), out var expires))
        { var seconds = Round((expires - now).TotalSeconds); return seconds > 0 ? $"expires in {Duration(seconds)} ({FormatWhen(lease["expiresAt"])})" : "due since " + FormatWhen(lease["expiresAt"]); }
        return state;
    }
    public static string Operation(JsonNode? op) => op is not JsonObject ? "" : Default(op["kind"], "job") + (Text(op["phase"]).Length > 0 ? " (" + Text(op["phase"]) + ")" : "") + (Text(op["initiator"]).Length > 0 ? " by " + Text(op["initiator"]) : "");
    public static JsonObject Usage(JsonNode? input, DateTimeOffset now)
    {
        var u = input as JsonObject ?? []; var known = DateTimeOffset.TryParse(Text(u["observedAt"]), out var at);
        var stale = StateJson.Truthy(u["stale"]) || known && (now - at).TotalSeconds > 30;
        var cpu = Number(u["cpuUsagePercent"]) is >= 0 and <= 100 and var c ? c : (double?)null;
        var demand = Number(u["memoryDemandBytes"]); var assigned = Number(u["memoryAssignedBytes"]);
        return new() { ["cpuPercent"] = cpu, ["ramPercent"] = demand.HasValue && assigned > 0 ? Pct(u["memoryDemandBytes"], u["memoryAssignedBytes"]) : null,
            ["cpu"] = cpu.HasValue ? N(Round(cpu.Value * 10) / 10) + "% CPU" : "CPU usage unavailable",
            ["ram"] = demand.HasValue ? $"{Bytes(u["memoryDemandBytes"])} RAM demand / {Bytes(u["memoryAssignedBytes"])} assigned" : assigned.HasValue ? Bytes(u["memoryAssignedBytes"]) + " RAM assigned · demand unavailable" : "RAM usage unavailable",
            ["disk"] = Number(u["diskFileBytes"]).HasValue ? Bytes(u["diskFileBytes"]) + " disk on host" : "Disk usage unavailable",
            ["sample"] = known ? (stale ? "Stale · last sample" : "Sampled") + " " + at.UtcDateTime.ToString("HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) : "Usage unavailable on this host", ["stale"] = stale };
    }
    public static JsonObject Vm(JsonNode? input, DateTimeOffset now)
    {
        var v = input as JsonObject ?? []; var r = new JsonObject();
        foreach (var k in new[] { "name", "owner", "parent" }) r[k] = Text(v[k]);
        r["kind"] = Default(v["kind"], "primary").ToLowerInvariant(); r["sharing"] = Default(v["sharing"], "private").ToLowerInvariant(); r["state"] = Default(v["state"], "unknown").ToLowerInvariant();
        foreach (var k in new[] { "shared", "deleting", "childCreationClosed" }) r[k] = StateJson.Boolean(v[k]) == true;
        r["tokenKind"] = Text(v["tokenKind"]).Length > 0 ? Text(v["tokenKind"]) : null;
        var hw = v["hardware"] as JsonObject ?? new JsonObject { ["cpus"] = Copy(v["cpu"]), ["ramMb"] = Number(v["ramGb"]) * 1024, ["diskGb"] = Copy(v["diskGb"]) };
        var resources = new List<string>(); if (Number(hw["cpus"]).HasValue) resources.Add(Text(hw["cpus"]) + " vCPU"); if (Number(hw["ramMb"]) is { } ram) resources.Add(Bytes(JsonValue.Create(ram * 1048576))); if (Number(hw["diskGb"]).HasValue) resources.Add(Text(hw["diskGb"]) + " GB disk");
        r["resources"] = resources.Count == 0 ? "—" : string.Join(" · ", resources); r["usage"] = Usage(v["resourceUsage"], now); r["lease"] = Lease(v["lease"], now); r["lifetime"] = Text(v["lease"]?["requested"]);
        r["overdue"] = StateJson.Boolean(v["lease"]?["overdue"]) == true || Text(v["lease"]?["state"]).ToLowerInvariant() == "overdue";
        r["operation"] = Operation(v["currentOperation"]); r["operationJobId"] = Text(v["currentOperation"]?["jobId"]);
        var guest = v["guest"] as JsonObject ?? []; var commit = Text(guest["constructCommit"]);
        var facts = new List<string> { "Construct " + (commit.Length > 0 ? commit[..Math.Min(12, commit.Length)] : "unknown"), "provisioned " + (guest["provisionedAt"] is null ? "unknown" : FormatWhen(guest["provisionedAt"])), "reinstalled " + (guest["reinstalledAt"] is null ? "unknown" : FormatWhen(guest["reinstalledAt"])) };
        if (StateJson.Truthy(guest["lastAttemptOutcome"])) facts.Add(("last attempt " + Text(guest["lastAttemptOutcome"]) + " " + (guest["lastAttemptAt"] is null ? "" : FormatWhen(guest["lastAttemptAt"]))).Trim()); r["guest"] = string.Join(" · ", facts);
        r["reservations"] = v["reservations"] is JsonObject reserve ? $"{Bytes(reserve["ramBytes"])} RAM · {(Number(reserve["cpus"]).HasValue ? Text(reserve["cpus"]) : "—")} vCPU · {Bytes(reserve["storageBytes"])} storage" : "—";
        r["children"] = new JsonArray(Array(v["children"]).Select(Text).Where(s => s.Length > 0).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        var allowed = new[] { "inspect", "start", "shutdown", "save", "restart", "delete", "share", "renew", "hardware", "media", "console", "forwardClient", "forwardHost", "addresses", "overrides", "rotateToken" };
        r["allowedActions"] = new JsonArray(Array(v["allowedActions"]).Select(Text).Where(allowed.Contains).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()); r["media"] = Array(v["media"]).Count; return r;
    }
    public static JsonArray Vms(JsonNode? list, DateTimeOffset now)
    {
        var rows = Array(list).Select(v => Vm(v, now)).Where(v => Text(v["name"]).Length > 0).ToArray(); var result = new JsonArray();
        var children = rows.Where(v => Text(v["kind"]) == "child").ToList();
        foreach (var parent in rows.Where(v => Text(v["kind"]) != "child").OrderBy(v => Text(v["name"]), StringComparer.Ordinal))
        { result.Add(parent); foreach (var child in children.Where(v => Text(v["parent"]) == Text(parent["name"])).OrderBy(v => Text(v["name"]), StringComparer.Ordinal).ToArray()) { result.Add(child); children.Remove(child); } }
        foreach (var child in children) result.Add(child); return result;
    }
    public static JsonArray Children(JsonNode? list, DateTimeOffset now) => new(Array(list).OfType<JsonObject>().Where(v => Text(v["name"]).Length > 0).OrderBy(v => Text(v["name"]), StringComparer.Ordinal).Select(v =>
    {
        var state = Default(v["state"], "unknown").ToLowerInvariant(); var op = Operation(v["currentOperation"]); var busy = op.Length > 0 || StateJson.Boolean(v["deleting"]) == true;
        bool Allows(string action) => v["allowedActions"] is not JsonArray a || a.Select(Text).Contains(action);
        return (JsonNode)new JsonObject { ["name"] = Text(v["name"]), ["state"] = state, ["lease"] = Lease(v["lease"], now), ["overdue"] = StateJson.Boolean(v["lease"]?["overdue"]) == true || Text(v["lease"]?["state"]).ToLowerInvariant() == "overdue", ["sharing"] = Text(v["sharing"]).ToLowerInvariant(), ["shared"] = Text(v["sharing"]).ToLowerInvariant() == "host", ["busy"] = busy, ["operation"] = busy && op.Length == 0 ? "deleting" : op, ["canShutdown"] = !busy && state is "running" or "paused" && Allows("shutdown"), ["canDelete"] = !busy && Allows("delete") };
    }).ToArray());
    public static JsonArray Config(JsonNode? config) => new(ConfigSections.Select(key =>
    {
        var section = config?[key] as JsonObject; var value = section?["value"] as JsonObject ?? section; var clean = value?.DeepClone().AsObject() ?? []; clean.Remove("source"); clean.Remove("updatedAt");
        return (JsonNode)new JsonObject { ["key"] = key, ["source"] = Default(section?["source"], section is null ? "default" : "stored"), ["updatedAt"] = section?["updatedAt"] is null ? "" : FormatWhen(section["updatedAt"]), ["expectedUpdatedAt"] = Text(section?["updatedAt"]).Length > 0 ? Text(section?["updatedAt"]) : null, ["text"] = StateJson.Stringify(clean, true), ["present"] = section is not null };
    }).ToArray());
    public static JsonObject Capabilities(JsonNode? body)
    {
        var rows = new JsonArray();
        void Walk(string prefix, JsonObject obj)
        { foreach (var (k, v) in obj) if (v is JsonObject o) Walk(prefix + k + ".", o); else if (k != "notes") rows.Add(new JsonObject { ["key"] = prefix + k, ["value"] = v is JsonArray a ? string.Join(", ", a.Select(Text)) : Text(v) }); }
        Walk("", body?["capabilities"] as JsonObject ?? []);
        foreach (var (k, v) in body?["policy"] as JsonObject ?? []) rows.Add(new JsonObject { ["key"] = "policy." + k, ["value"] = Text(v) });
        return new() { ["backend"] = Text(body?["backend"]), ["rows"] = rows, ["notes"] = Copy(body?["capabilities"]?["notes"]) ?? new JsonArray() };
    }
    public static JsonObject Job(JsonNode? input)
    {
        var j = input as JsonObject ?? []; var result = Strings(j, "id", "kind", "vmName", "owner", "initiator", "phase", "error");
        var state = Default(j["state"], "unknown").ToLowerInvariant(); result["state"] = state; result["created"] = FormatWhen(j["created"]); result["completed"] = FormatWhen(j["completed"] ?? j["completedAt"]);
        var terminal = state is "succeeded" or "failed" or "cancelled"; result["terminal"] = terminal; result["cancellable"] = !terminal;
        result["retry"] = state == "failed" && Text(j["kind"]) is "child-delete" or "parent-cascade-delete" && Text(j["vmName"]).Length > 0 ? new JsonObject { ["action"] = "deleteVm", ["name"] = Text(j["vmName"]), ["kind"] = Text(j["kind"]) == "parent-cascade-delete" ? "primary" : "child" } : state == "failed" && Text(j["kind"]) == "media-cleanup" ? new JsonObject { ["action"] = "mediaCleanup" } : null; return result;
    }
    public static JsonObject Audit(JsonNode? input)
    { var a = input as JsonObject ?? []; var result = Strings(a, "actor", "action", "target", "detail"); result["at"] = FormatWhen(a["at"] ?? a["timestamp"] ?? a["created"]); return result; }
    public static JsonObject Media(JsonNode? input)
    {
        var m = input as JsonObject ?? []; var result = Strings(m, "id", "owner", "name", "role", "source", "sourceUrl", "error", "jobId", "dedicatedTo");
        result["state"] = Default(m["state"], "unknown").ToLowerInvariant(); result["size"] = Bytes(m["sizeBytes"]); result["reserved"] = Bytes(m["reservedBytes"]); result["created"] = FormatWhen(m["created"]); result["readyAt"] = FormatWhen(m["readyAt"]); result["references"] = Number(m["references"]) ?? 0; result["deletable"] = (Number(m["references"]) ?? 0) == 0; return result;
    }
    public static JsonObject AllowanceForm(JsonNode? input)
    {
        var a = input as JsonObject ?? []; var result = new JsonObject();
        foreach (var k in new[] { "allowChildCreation", "allowNeverLifetime", "allowSharing" }) result[k] = StateJson.Boolean(a[k]) is bool b ? b ? "true" : "false" : "";
        foreach (var k in new[] { "maxRetainedChildren", "cpuBudget" }) result[k] = Number(a[k]).HasValue ? Text(a[k]) : "";
        foreach (var k in new[] { "ramBudget", "storageBudget" }) result[k + "GiB"] = Number(a[k + "Bytes"]) is { } n ? N(Round(n / 1073741824 * 100) / 100) : "";
        result["maxChildLifetime"] = Number(a["maxChildLifetimeSeconds"]) is { } s ? s % 86400 == 0 ? N(s / 86400) + "d" : s % 3600 == 0 ? N(s / 3600) + "h" : N(Round(s / 60)) + "m" : ""; return result;
    }
    public static string AllowanceText(JsonNode? input)
    {
        if (input is not JsonObject e) return "—";
        string Num(string k, string fallback) => Number(e[k]).HasValue ? Text(e[k]) : fallback;
        var parts = new List<string> { Num("maxPrimaries", "—") + " primaries", StateJson.Boolean(e["allowChildCreation"]) == false ? "no children" : Num("maxRetainedChildren", "—") + " children", "CPU " + Num("cpuBudget", "no budget"), "RAM " + (Number(e["ramBudgetBytes"]).HasValue ? Bytes(e["ramBudgetBytes"]) : "no budget"), "storage " + (Number(e["storageBudgetBytes"]).HasValue ? Bytes(e["storageBudgetBytes"]) : "no budget"), "lifetime ≤ " + (Number(e["maxChildLifetimeSeconds"]) is { } seconds ? Duration(seconds) : "unlimited") + (StateJson.Boolean(e["allowNeverLifetime"]) == false ? " (no 'never')" : ""), StateJson.Boolean(e["allowSharing"]) == false ? "no sharing" : "sharing", StateJson.Boolean(e["allowHostForwards"]) == false ? "no host forwards" : "host forwards" };
        if (e["usage"] is JsonObject u) parts.Add($"in use: {N(Number(u["primaries"]) ?? 0)} primaries, {N(Number(u["children"]) ?? 0)} children, {N(Number(u["cpus"]) ?? 0)} vCPU, {Bytes(u["ramBytes"])} RAM, {Bytes(u["storageBytes"])} storage"); return string.Join(" · ", parts);
    }
    public static JsonObject User(JsonNode? input)
    {
        var u = input as JsonObject ?? []; return new() { ["name"] = Text(u["name"]), ["role"] = Default(u["role"], "user").ToLowerInvariant(), ["enabled"] = StateJson.Boolean(u["enabled"]) != false, ["maxVms"] = Number(u["maxVms"]), ["allowHostForwards"] = StateJson.Boolean(u["allowHostForwards"]) != false, ["created"] = FormatWhen(u["created"]), ["primaries"] = Number(u["vms"]?["primaries"]) ?? 0, ["children"] = Number(u["vms"]?["children"]) ?? 0, ["tokens"] = Number(u["tokens"]) ?? 0, ["allowance"] = AllowanceForm(u["allowance"]), ["effective"] = AllowanceText(u["effective"]) };
    }
    private static JsonObject Strings(JsonObject obj, params string[] keys)
    { var result = new JsonObject(); foreach (var key in keys) result[key] = Text(obj[key]); return result; }
    public static JsonArray Map(JsonNode? value, Func<JsonNode?, JsonObject> map) => new(Array(value).Select(v => (JsonNode)map(v)).ToArray());
}
