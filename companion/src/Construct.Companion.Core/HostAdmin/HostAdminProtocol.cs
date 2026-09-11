using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Companion.Core.State;
namespace Construct.Companion.Core.HostAdmin;

public static partial class HostAdminProtocol
{
    public static readonly string[] Tabs = ["overview", "vms", "users", "media", "operations", "config", "maintenance"];
    public static readonly string[] ConfigSections = ["capacity", "userDefaults", "userCaps", "lifecycle", "media", "network", "updates"];
    public static string Text(JsonNode? node) => StateJson.Trim(node is null ? "" : StateJson.String(node));
    public static JsonObject Features(JsonNode? health)
    {
        var flags = (health?["apiFeatures"] as JsonArray ?? []).Select(Text).ToHashSet();
        return new() { ["hostAdmin"] = flags.Contains("host-admin"), ["children"] = flags.Contains("children"), ["media"] = flags.Contains("media"), ["console"] = flags.Contains("console"), ["updates"] = flags.Contains("updates"), ["network"] = flags.Contains("network") };
    }
    public static JsonArray TabsFor(JsonObject features) => new(Tabs.Select((id, i) => (JsonNode)new JsonObject
    { ["id"] = id, ["label"] = new[] { "Overview", "VMs", "Users", "Media", "Operations", "Configuration", "Maintenance" }[i], ["available"] = StateJson.Boolean(features[id == "maintenance" ? "updates" : "hostAdmin"]) == true, ["reason"] = StateJson.Boolean(features[id == "maintenance" ? "updates" : "hostAdmin"]) == true ? "" : "not available on this host version" }).ToArray());
    public static JsonObject ClampIdlePolicy(JsonObject policy, double max)
    {
        var requested = StateJson.CoerceNumber(policy["timeoutMinutes"]); var timeout = double.IsFinite(requested) && requested > 0 ? Math.Floor(requested) : 0;
        var clamped = max > 0 && timeout > max; if (clamped) timeout = max;
        return new() { ["timeoutMinutes"] = timeout, ["action"] = NormalizeIdleAction(policy["action"]), ["clamped"] = clamped };
    }
    private static string NormalizeIdleAction(JsonNode? action) => Text(action).ToLowerInvariant() == "shutdown" ? "shutdown" : "save";
    public static JsonObject? IdlePolicy(JsonNode? response)
    {
        if (response is not JsonObject value) return null;
        var timeout = StateJson.CoerceNumber(value["timeoutMinutes"]); if (!double.IsFinite(timeout)) return null;
        var max = StateJson.CoerceNumber(value["maxTimeoutMinutes"]);
        return new() { ["timeoutMinutes"] = Math.Max(0, Math.Floor(timeout)), ["action"] = NormalizeIdleAction(value["action"]), ["maxTimeoutMinutes"] = double.IsFinite(max) && max > 0 ? Math.Floor(max) : 0, ["clamped"] = StateJson.Boolean(value["clamped"]) == true };
    }
    public static JsonObject ParseLifetime(JsonNode? input)
    {
        var text = Text(input).ToLowerInvariant();
        JsonObject Error(string reason) => new() { ["ok"] = false, ["reason"] = reason };
        if (text.Length == 0) return Error("a lifetime is required (e.g. 12h, 3d, or never)");
        if (text == "never") return new() { ["ok"] = true, ["seconds"] = null, ["text"] = "never" };
        var match = Regex.Match(text, "^([1-9][0-9]*)([mhd])$");
        if (!match.Success) return Error($"\"{Text(input)}\" is not a lifetime: use <n>m, <n>h, <n>d or never");
        var seconds = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * (match.Groups[2].Value == "m" ? 60 : match.Groups[2].Value == "h" ? 3600 : 86400);
        if (seconds > 9007199254740991) return Error("the lifetime is too large");
        if (seconds < 300) return Error("the minimum lifetime is 5m");
        return new() { ["ok"] = true, ["seconds"] = seconds, ["text"] = text };
    }
    public static JsonObject ChildDeleteConfirmation(JsonObject child)
    {
        var name = Text(child["name"]); if (name.Length == 0) name = "this child VM";
        var sharing = Text(child["sharing"]).ToLowerInvariant(); var shared = sharing == "host" || StateJson.Boolean(child["shared"]) == true;
        return new() { ["title"] = $"Delete the child VM \"{name}\"?", ["detail"] = name + " is " + (shared ? "SHARED HOST-WIDE — other users may be using it" : sharing == "private" ? "private" : "of unknown sharing scope — other users may be using it") + (Text(child["state"]).Length > 0 ? " and currently " + Text(child["state"]) : "") + ".\n\nIts disk, saved state and dedicated media are removed permanently. A running VM is powered off for deletion.", ["confirmLabel"] = "Delete " + name, ["shared"] = shared };
    }
    public static string FormatWhen(JsonNode? value)
    {
        var text = Text(value); return text.Length == 0 ? "—" : DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : text;
    }
    public static JsonObject ParseForm(string kind, JsonObject form)
    {
        var body = new JsonObject(); var problems = new JsonArray();
        void Problem(string key, string reason) => problems.Add(new JsonObject { ["field"] = key, ["reason"] = reason });
        JsonNode? Tri(string key)
        {
            var value = Text(form[key]).ToLowerInvariant();
            if (value is "" or "inherit") return null;
            if (value is "true" or "yes" or "on") return JsonValue.Create(true);
            if (value is "false" or "no" or "off") return JsonValue.Create(false);
            Problem(key, "must be true, false or empty (inherit)"); return null;
        }
        JsonNode? Number(string key, bool gib = false)
        {
            if (Text(form[key]).Length == 0) return null;
            var n = StateJson.CoerceNumber(form[key]);
            if (!double.IsFinite(n) || n < 0 || !gib && n != Math.Truncate(n)) { Problem(key, gib ? "must be a number of GiB ≥ 0, or empty (inherit)" : "must be a whole number ≥ 0, or empty (inherit)"); return null; }
            return JsonValue.Create(gib ? Math.Floor(n * 1073741824 + .5) : n);
        }
        if (kind is "allowance" or "overrides")
        {
            body["allowChildCreation"] = Tri("allowChildCreation"); body["maxRetainedChildren"] = Number("maxRetainedChildren");
            if (kind == "allowance") { body["cpuBudget"] = Number("cpuBudget"); body["ramBudgetBytes"] = Number("ramBudgetGiB", true); body["storageBudgetBytes"] = Number("storageBudgetGiB", true); }
            body["maxChildLifetimeSeconds"] = null;
            if (Text(form["maxChildLifetime"]).Length > 0)
            {
                var life = ParseLifetime(form["maxChildLifetime"]);
                if (StateJson.Boolean(life["ok"]) != true) Problem("maxChildLifetime", Text(life["reason"]));
                else if (life["seconds"] is null) Problem("maxChildLifetime", "a maximum cannot be 'never'; leave it empty for no maximum");
                else body["maxChildLifetimeSeconds"] = life["seconds"]?.DeepClone();
            }
            body["allowNeverLifetime"] = Tri("allowNeverLifetime"); body["allowSharing"] = Tri("allowSharing");
        }
        else
        {
            if (kind == "newUser") { body["name"] = Text(form["name"]); if (Text(form["name"]).Length == 0) Problem("name", "a user name is required (DOMAIN\\user or a plain name)"); }
            var role = Text(form["role"]).ToLowerInvariant(); if (role.Length == 0 && kind == "newUser") role = "user";
            if (kind == "newUser") body["role"] = role;
            if (role.Length > 0) { if (role is not ("admin" or "user")) Problem("role", "must be admin or user"); else body["role"] = role; }
            if (kind != "newUser" && Tri("enabled") is { } enabled) body["enabled"] = enabled;
            if (Number("maxVms") is { } max) body["maxVms"] = max;
            if (Tri("allowHostForwards") is { } forwards) body["allowHostForwards"] = forwards;
            if (body.Count == 0 && problems.Count == 0) Problem("", "nothing to change");
        }
        return new() { ["ok"] = problems.Count == 0, ["body"] = body, ["problems"] = problems };
    }
}
