using System.Text.Json;
using System.Text.Json.Nodes;

namespace Constructd.Sync.Profiles;

/// <summary>One finding of the profile checks, attributed to the top-level field it is about.</summary>
public sealed record ProfileProblem(string Field, string Message);

/// <summary>
/// The profile checks and canonical form (projects/project.schema.json; the same rules as the
/// Companion's ProfileCodec), over decoded JSON. <see cref="Normalize"/> fills defaults
/// (<c>forwards.to</c> is <c>client</c>, the inferred MCP <c>type</c>, empty lists) and puts the
/// known fields in their canonical order; fields it does not know are kept after them.
/// </summary>
public static partial class ProfileRules
{
    public static readonly string[] KnownFields = ["name", "repos", "sdks", "mcp", "hostPackages", "provisionCommands", "forwards", "tests"];

    private static readonly string[] Agents = ["claude", "claude-code", "codex", "opencode"];

    // ---- canonical form -------------------------------------------------------------------------

    /// <summary>
    /// The canonical form with defaults filled in. <c>forwards</c> is always present here (empty
    /// when the profile has none) so the merge can treat it like the other lists;
    /// <see cref="Finish"/> drops it again when empty, as the canonical file form does.
    /// </summary>
    public static JsonObject Normalize(JsonObject profile)
    {
        var o = profile;
        var result = new JsonObject { ["name"] = o["name"]?.DeepClone() };

        result["repos"] = Map(o["repos"], item =>
        {
            if (item is not JsonObject r) return item?.DeepClone();
            var e = new JsonObject { ["url"] = r["url"]?.DeepClone() };
            if (r.ContainsKey("directory")) e["directory"] = r["directory"]?.DeepClone();
            foreach (var (k, v) in r) if (k is not ("url" or "directory")) e[k] = v?.DeepClone();
            return e;
        });

        var sdks = new JsonObject();
        if (o["sdks"] is JsonObject sdkSource)
            foreach (var (k, v) in sdkSource) if (v is not JsonArray { Count: 0 }) sdks[k] = v?.DeepClone();
        result["sdks"] = o["sdks"] is null or JsonObject ? sdks : o["sdks"]!.DeepClone();

        result["mcp"] = Map(o["mcp"], item =>
        {
            if (item is not JsonObject m) return item?.DeepClone();
            var type = Text(m["type"]);
            if (type.Length == 0) type = Str(m["command"]) ? "stdio" : Str(m["url"]) ? "http" : "";
            var e = new JsonObject { ["name"] = m["name"]?.DeepClone() };
            if (type.Length > 0) e["type"] = type;
            string[] order = type == "http" ? ["url", "headers", "bearerTokenEnvVar"] : ["command", "args", "env"];
            foreach (var key in order)
                if (m.ContainsKey(key) && m[key] is not (JsonArray { Count: 0 } or JsonObject { Count: 0 })) e[key] = m[key]?.DeepClone();
            foreach (var (k, v) in m) if (!e.ContainsKey(k) && k != "type" && !order.Contains(k)) e[k] = v?.DeepClone();
            return e;
        });

        result["hostPackages"] = o["hostPackages"]?.DeepClone() ?? new JsonArray();
        result["provisionCommands"] = o["provisionCommands"]?.DeepClone() ?? new JsonArray();

        result["forwards"] = Map(o["forwards"], item =>
        {
            if (item is not JsonObject f) return item?.DeepClone();
            var e = new JsonObject { ["port"] = Port(f["port"]) is { } port ? JsonNode.Parse(port.ToString(System.Globalization.CultureInfo.InvariantCulture)) : f["port"]?.DeepClone() };
            // An HTTPS forward is served by the host, whatever "to" said; "https" counts only when it is true,
            // and "upstreamTls" and "name" only go with it (the guest's canonical form, in its key order).
            var https = True(f["https"]);
            e["to"] = https ? "host" : f.ContainsKey("to") ? f["to"]?.DeepClone() : "client";
            if (https)
            {
                e["https"] = true;
                if (f.ContainsKey("upstreamTls")) e["upstreamTls"] = f["upstreamTls"]?.DeepClone();
                if (f.ContainsKey("name")) e["name"] = f["name"]?.DeepClone();
            }

            if (Str(f["label"])) e["label"] = f["label"]!.DeepClone();
            foreach (var (k, v) in f) if (k is not ("port" or "to" or "label" or "https" or "upstreamTls" or "name")) e[k] = v?.DeepClone();
            return e;
        });

        result["tests"] = o["tests"]?.DeepClone() ?? new JsonObject();

        foreach (var (k, v) in o) if (!KnownFields.Contains(k)) result[k] = v?.DeepClone();
        return result;
    }

    /// <summary>The stored form of a normalised profile: an empty <c>forwards</c> is left out.</summary>
    public static JsonObject Finish(JsonObject normalized)
    {
        var result = normalized.DeepClone().AsObject();
        if (result["forwards"] is JsonArray { Count: 0 }) result.Remove("forwards");
        return result;
    }

    /// <summary>Two profiles with the same canonical form (null is the absent profile).</summary>
    public static bool Equivalent(JsonNode? a, JsonNode? b) => (a, b) switch
    {
        (null, null) => true,
        (JsonObject x, JsonObject y) => JsonNode.DeepEquals(Finish(Normalize(x)), Finish(Normalize(y))),
        _ => false,
    };

    // ---- checks ---------------------------------------------------------------------------------

    /// <summary>The checks a profile write passes (schema and the write-path rules).</summary>
    public static IReadOnlyList<ProfileProblem> Validate(JsonObject obj)
    {
        var errors = new List<ProfileProblem>();
        void Add(string field, string message) => errors.Add(new ProfileProblem(field, message));

        foreach (var k in obj.Select(p => p.Key)) if (!KnownFields.Contains(k)) Add(k, $"unknown key \"{k}\"");
        if (!Str(obj["name"])) Add("name", "\"name\" must be a non-empty string");
        else
        {
            var nm = Text(obj["name"]);
            if (nm.Contains('/') || nm.Contains('\\') || nm == "." || nm.Contains("..", StringComparison.Ordinal)) Add("name", "\"name\" contains illegal path characters");
            if (nm.Trim().ToLowerInvariant() is "default" or "project.schema") Add("name", $"\"{nm}\" is a reserved name");
        }

        if (obj.ContainsKey("repos"))
        {
            if (obj["repos"] is not JsonArray repos) Add("repos", "\"repos\" must be an array");
            else for (var i = 0; i < repos.Count; i++)
            {
                if (repos[i] is not JsonObject r) { Add("repos", $"repos[{i}] must be an object"); continue; }
                foreach (var k in r.Select(p => p.Key)) if (k is not ("url" or "directory")) Add("repos", $"repos[{i}] unknown key \"{k}\"");
                if (!Str(r["url"])) Add("repos", $"repos[{i}].url must be a non-empty string");
                if (r.ContainsKey("directory") && !Str(r["directory"])) Add("repos", $"repos[{i}].directory must be a non-empty string");
            }
        }

        if (obj.ContainsKey("sdks"))
        {
            if (obj["sdks"] is not JsonObject sdks) Add("sdks", "\"sdks\" must be an object");
            else foreach (var (k, v) in sdks)
                if (!Str(v) && !(v is JsonArray a && a.All(Str))) Add("sdks", $"sdks.{k} must be a non-empty string or an array of non-empty strings");
        }

        if (obj.ContainsKey("mcp"))
        {
            if (obj["mcp"] is not JsonArray mcp) Add("mcp", "\"mcp\" must be an array");
            else for (var i = 0; i < mcp.Count; i++)
            {
                if (mcp[i] is not JsonObject m) { Add("mcp", $"mcp[{i}] must be an object"); continue; }
                if (!Str(m["name"])) Add("mcp", $"mcp[{i}].name must be a non-empty string");
                var type = Text(m["type"]);
                if (m["type"] != null && type is not ("stdio" or "http")) { Add("mcp", $"mcp[{i}].type must be \"stdio\" or \"http\""); continue; }
                if (type.Length == 0) type = Str(m["command"]) ? "stdio" : Str(m["url"]) ? "http" : "";
                if (type.Length == 0) { Add("mcp", $"mcp[{i}] needs a \"command\" (stdio) or \"url\" (http)"); continue; }
                string[] allowed = ["name", "type", "agents", "enabled", .. type == "stdio" ? new[] { "command", "args", "env" } : ["url", "headers", "bearerTokenEnvVar"]];
                foreach (var k in m.Select(p => p.Key)) if (!allowed.Contains(k)) Add("mcp", $"mcp[{i}] unknown key \"{k}\" for a {type} server");
                var field = type == "stdio" ? "command" : "url";
                if (!Str(m[field])) Add("mcp", $"mcp[{i}].{field} must be a non-empty string");
                if (type == "stdio")
                {
                    if (m.ContainsKey("args") && !(m["args"] is JsonArray a && a.All(StringValue))) Add("mcp", $"mcp[{i}].args must be an array of strings");
                    if (m.ContainsKey("env") && !StringMap(m["env"])) Add("mcp", $"mcp[{i}].env must be an object of string values");
                }
                else
                {
                    if (m.ContainsKey("headers") && !StringMap(m["headers"])) Add("mcp", $"mcp[{i}].headers must be an object of string values");
                    if (m.ContainsKey("bearerTokenEnvVar") && !Str(m["bearerTokenEnvVar"])) Add("mcp", $"mcp[{i}].bearerTokenEnvVar must be a non-empty string");
                }

                if (m.ContainsKey("agents") && !(m["agents"] is JsonArray agents && agents.Count > 0 && agents.All(x => Agents.Contains(Text(x)))))
                    Add("mcp", $"mcp[{i}].agents must be a non-empty array of known agents");
                if (m.ContainsKey("enabled") && !(m["enabled"] is JsonValue ev && ev.GetValueKind() is JsonValueKind.True or JsonValueKind.False))
                    Add("mcp", $"mcp[{i}].enabled must be a boolean");
            }
        }

        foreach (var key in new[] { "hostPackages", "provisionCommands" })
            if (obj.ContainsKey(key) && !(obj[key] is JsonArray a && a.All(Str))) Add(key, $"\"{key}\" must be an array of non-empty strings");

        if (obj.ContainsKey("forwards"))
        {
            if (obj["forwards"] is not JsonArray forwards) Add("forwards", "\"forwards\" must be an array");
            else for (var i = 0; i < forwards.Count; i++)
            {
                if (forwards[i] is not JsonObject f) { Add("forwards", $"forwards[{i}] must be an object with a \"port\""); continue; }
                foreach (var k in f.Select(p => p.Key)) if (k is not ("port" or "label" or "to" or "https" or "name" or "upstreamTls")) Add("forwards", $"forwards[{i}] unknown key \"{k}\"");
                if (Port(f["port"]) is null) Add("forwards", $"forwards[{i}].port must be an integer from 1 to 65535");
                if (f.ContainsKey("label") && !StringValue(f["label"])) Add("forwards", $"forwards[{i}].label must be a string");
                if (f.ContainsKey("to") && Text(f["to"]) is not ("client" or "host")) Add("forwards", $"forwards[{i}].to must be \"client\" or \"host\"");
                // An HTTPS forward with the host's certificate: it says what the port speaks, the host never guesses.
                var https = True(f["https"]);
                if (f.ContainsKey("https") && !Bool(f["https"])) Add("forwards", $"forwards[{i}].https must be true or false");
                if (f.ContainsKey("name") && !ForwardName(f["name"])) Add("forwards", $"forwards[{i}].name must be one DNS label: up to 63 lowercase letters, digits and hyphens, starting and ending with a letter or digit");
                if (f.ContainsKey("upstreamTls") && !Bool(f["upstreamTls"])) Add("forwards", $"forwards[{i}].upstreamTls must be true or false");
                if (https && StringValue(f["to"]) && Text(f["to"]) == "client") Add("forwards", $"forwards[{i}].to must be \"host\" or left out: the host serves an HTTPS forward");
                if (https && !f.ContainsKey("upstreamTls")) Add("forwards", $"forwards[{i}].upstreamTls is required with \"https\": true: false when the port serves plain HTTP, true when it speaks TLS itself");
                if (f.ContainsKey("name") && !https) Add("forwards", $"forwards[{i}].name needs \"https\": true");
                if (f.ContainsKey("upstreamTls") && !https) Add("forwards", $"forwards[{i}].upstreamTls needs \"https\": true");
            }
        }

        if (obj.ContainsKey("tests") && obj["tests"] is not JsonObject) Add("tests", "\"tests\" must be an object");
        return errors;
    }

    /// <summary>The directory a repository is checked out into (bin/checkout-projects.sh).</summary>
    public static string EffectiveDirectory(JsonNode? repo)
    {
        if (repo is JsonObject r && Str(r["directory"])) return Text(r["directory"]);
        var url = repo is JsonObject o ? Text(o["url"]) : "";
        if (url.EndsWith(".git", StringComparison.Ordinal)) url = url[..^4];
        return url.Split('/')[^1];
    }

    // ---- helpers --------------------------------------------------------------------------------

    internal static bool Str(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>().Trim().Length > 0;

    public static string Text(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : n?.ToJsonString() ?? "";

    private static bool StringValue(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String;

    private static bool Bool(JsonNode? n) => n is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False;

    private static bool True(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    /// <summary>One DNS label, as <c>construct expose --name</c> takes it.</summary>
    private static bool ForwardName(JsonNode? n) => StringValue(n) && ForwardNamePattern().IsMatch(n!.GetValue<string>());

    [System.Text.RegularExpressions.GeneratedRegex(@"\A[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ForwardNamePattern();

    private static bool StringMap(JsonNode? n) => n is JsonObject o && o.All(p => StringValue(p.Value));

    private static int? Port(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number
        && double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
        && double.IsFinite(d) && Math.Floor(d) == d && d is >= 1 and <= 65535 ? (int)d : null;

    private static JsonNode? Map(JsonNode? list, Func<JsonNode?, JsonNode?> item) =>
        list switch
        {
            null => new JsonArray(),
            JsonArray a => new JsonArray(a.Select(item).ToArray()),
            _ => list.DeepClone(),
        };
}
