using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Construct.Companion.Core.ConfigSync;

// Strict validation precedes canonicalization: never silently repair an agent's edit.
public static class ProfileCodec
{
    private static bool Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0;
    private static bool StringValue(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out _);
    private static string Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString() ?? "";
    private static bool Map(JsonNode? n) => n is JsonObject o && o.All(p => StringValue(p.Value));
    private static readonly string[] Agents = ["claude", "codex", "opencode"];
    public static IReadOnlyList<string> ValidateProfile(string name, JsonNode? node)
    {
        if (node is not JsonObject obj) return ["profile is not a JSON object"];
        var errors = new List<string>();
        foreach (var k in obj.Select(p => p.Key)) if (!new[] { "name", "repos", "sdks", "mcp", "hostPackages", "provisionCommands", "tests" }.Contains(k)) errors.Add($"unknown key \"{k}\"");
        if (!Str(obj["name"])) errors.Add("\"name\" must be a non-empty string");
        else
        {
            var nm = Text(obj["name"]);
            if (nm.Contains('/') || nm.Contains('\\') || nm == "." || nm.Contains("..", StringComparison.Ordinal)) errors.Add("\"name\" contains illegal path characters");
            if (name.Trim().Length > 0 && nm != name.Trim()) errors.Add($"\"name\" is \"{nm}\" but the profile file is \"{name.Trim()}\"");
        }
        if (obj.ContainsKey("repos"))
        {
            if (obj["repos"] is not JsonArray repos) errors.Add("\"repos\" must be an array");
            else for (var i = 0; i < repos.Count; i++)
            {
                if (repos[i] is not JsonObject r) { errors.Add($"repos[{i}] must be an object"); continue; }
                foreach (var k in r.Select(p => p.Key)) if (k != "url" && k != "directory") errors.Add($"repos[{i}] unknown key \"{k}\"");
                if (!Str(r["url"])) errors.Add($"repos[{i}].url must be a non-empty string");
                if (r.ContainsKey("directory") && !Str(r["directory"])) errors.Add($"repos[{i}].directory must be a non-empty string");
            }
        }
        if (obj.ContainsKey("sdks"))
        {
            if (obj["sdks"] is not JsonObject sdks) errors.Add("\"sdks\" must be an object");
            else foreach (var (k, v) in sdks) if (!Str(v) && !(v is JsonArray a && a.All(Str))) errors.Add($"sdks.{k} must be a non-empty string or an array of non-empty strings");
        }
        if (obj.ContainsKey("mcp"))
        {
            if (obj["mcp"] is not JsonArray mcp) errors.Add("\"mcp\" must be an array");
            else for (var i = 0; i < mcp.Count; i++)
            {
                if (mcp[i] is not JsonObject m) { errors.Add($"mcp[{i}] must be an object with \"name\" plus \"command\" (stdio) or \"url\" (http); see docs/projects.md"); continue; }
                if (!Str(m["name"])) errors.Add($"mcp[{i}].name must be a non-empty string");
                var type = Text(m["type"]);
                if (m["type"] != null && type != "stdio" && type != "http") { errors.Add($"mcp[{i}].type must be \"stdio\" or \"http\""); continue; }
                if (type.Length == 0) type = Str(m["command"]) ? "stdio" : Str(m["url"]) ? "http" : "";
                if (type.Length == 0) { errors.Add($"mcp[{i}] needs a \"command\" (stdio) or \"url\" (http)"); continue; }
                var allowed = new[] { "name", "type", "agents", "enabled" }.Concat(type == "stdio" ? ["command", "args", "env"] : new[] { "url", "headers", "bearerTokenEnvVar" });
                foreach (var k in m.Select(p => p.Key)) if (!allowed.Contains(k)) errors.Add($"mcp[{i}] unknown key \"{k}\" for a {type} server");
                var field = type == "stdio" ? "command" : "url";
                if (!Str(m[field])) errors.Add($"mcp[{i}].{field} must be a non-empty string");
                if (type == "stdio")
                {
                    if (m.ContainsKey("args") && !(m["args"] is JsonArray a && a.All(StringValue))) errors.Add($"mcp[{i}].args must be an array of strings");
                    if (m.ContainsKey("env") && !Map(m["env"])) errors.Add($"mcp[{i}].env must be an object of string values");
                }
                else
                {
                    if (m.ContainsKey("headers") && !Map(m["headers"])) errors.Add($"mcp[{i}].headers must be an object of string values");
                    if (m.ContainsKey("bearerTokenEnvVar") && !Str(m["bearerTokenEnvVar"])) errors.Add($"mcp[{i}].bearerTokenEnvVar must be a non-empty string");
                }
                if (m.ContainsKey("agents") && !(m["agents"] is JsonArray agents && agents.Count > 0 && agents.All(a => Agents.Contains(Text(a))))) errors.Add($"mcp[{i}].agents must be a non-empty array from {string.Join('/', Agents)}");
                if (m.ContainsKey("enabled") && !(m["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out _))) errors.Add($"mcp[{i}].enabled must be a boolean");
            }
        }
        foreach (var key in new[] { "hostPackages", "provisionCommands" }) if (obj.ContainsKey(key) && !(obj[key] is JsonArray a && a.All(Str))) errors.Add($"\"{key}\" must be an array of non-empty strings");
        if (obj.ContainsKey("tests") && obj["tests"] is not JsonObject) errors.Add("\"tests\" must be an object");
        return errors;
    }
    private static JsonNode? OrderObjectKeys(JsonNode? node)
    {
        if (node is JsonObject obj) { var result=new JsonObject(); foreach(var key in ConfigSyncRules.JsKeys(obj.Select(p=>p.Key))) result[key]=OrderObjectKeys(obj[key]); return result; }
        if (node is JsonArray array) return new JsonArray(array.Select(OrderObjectKeys).ToArray());
        return node?.DeepClone();
    }
    private static JsonNode? NormalizeNumbers(JsonNode? node)
    {
        if (node is JsonObject obj) { var result = new JsonObject(); foreach (var pair in obj) result[pair.Key] = NormalizeNumbers(pair.Value); return result; }
        if (node is JsonArray array) return new JsonArray(array.Select(NormalizeNumbers).ToArray());
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.Number)
        {
            var n = value.GetValue<double>();
            if (!double.IsFinite(n)) return null;
            if (n == 0) return JsonValue.Create(0);
            var text = n.ToString("R", System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant();
            var e = text.IndexOf('e');
            if (e >= 0)
            {
                var exponent = int.Parse(text[(e + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                var mantissa = text[..e];
                if (exponent >= -6 && exponent < 21)
                {
                    var negative = mantissa.StartsWith('-'); if (negative) mantissa = mantissa[1..];
                    var digits = mantissa.Replace(".", ""); var point = exponent + 1;
                    text = (negative ? "-" : "") + (point <= 0 ? "0." + new string('0', -point) + digits : point >= digits.Length ? digits + new string('0', point - digits.Length) : digits.Insert(point, "."));
                }
                else text = mantissa + "e" + (exponent >= 0 ? "+" : "-") + Math.Abs(exponent);
            }
            return JsonNode.Parse(text);
        }
        return node?.DeepClone();
    }
    public static CanonicalResult CanonicalizeProfileText(string name, string raw)
    {
        JsonNode? node;
        try { node = OrderObjectKeys(JsonNode.Parse(raw)); }
        catch (JsonException) { return new(false, Reason: "cannot be parsed as JSON"); }
        var errors = ValidateProfile(name, node);
        if (errors.Count > 0) return new(false, Reason: ConfigSyncRules.RedactGitOutput("is not a valid profile: " + string.Join("; ", errors)));
        var o = (JsonObject)node!;
        var repos = new JsonArray();
        foreach (var r in (o["repos"] as JsonArray ?? []).Cast<JsonObject>()) { var e = new JsonObject { ["url"] = r["url"]!.DeepClone() }; if (r.ContainsKey("directory")) e["directory"] = r["directory"]!.DeepClone(); repos.Add(e); }
        var sdks = new JsonObject(); foreach (var (key, value) in o["sdks"] as JsonObject ?? []) if (value is not JsonArray a || a.Count > 0) sdks[key] = value!.DeepClone();
        var mcp = new JsonArray();
        foreach (var m in (o["mcp"] as JsonArray ?? []).Cast<JsonObject>())
        {
            var type = Text(m["type"]); if (type.Length == 0) type = Str(m["command"]) ? "stdio" : "http";
            var e = new JsonObject { ["name"] = m["name"]!.DeepClone(), ["type"] = type };
            foreach (var key in type == "stdio" ? new[] { "command", "args", "env" } : new[] { "url", "headers", "bearerTokenEnvVar" })
                if (m[key] is { } value && !(value is JsonArray a && a.Count == 0) && !(value is JsonObject map && map.Count == 0)) e[key] = value.DeepClone();
            if (m["agents"] != null) e["agents"] = m["agents"]!.DeepClone();
            if (m.ContainsKey("enabled")) e["enabled"] = m["enabled"]!.DeepClone();
            mcp.Add(e);
        }
        return new(true, ConfigSyncRules.Serialize(new JsonObject { ["name"] = name.Trim(), ["repos"] = repos, ["sdks"] = sdks, ["mcp"] = mcp, ["hostPackages"] = o["hostPackages"]?.DeepClone() ?? new JsonArray(), ["provisionCommands"] = o["provisionCommands"]?.DeepClone() ?? new JsonArray(), ["tests"] = NormalizeNumbers(o["tests"]) ?? new JsonObject() }));
    }
}
public sealed record CanonicalResult(bool Ok,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Content = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null)
{ public override string ToString() => $"CanonicalResult {{ Ok = {Ok} }}"; }
