using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Construct.Companion.Core.Runtime;

// JSON boundary helpers retain JavaScript's null/default and whitespace rules.
public static class RuntimeJson
{
    public static string Text(JsonNode? node) => node switch
    {
        null => "", JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonArray a => string.Join(',', a.Select(Text)), JsonObject => "[object Object]", _ => node.ToJsonString()
    };
    public static string Str(this JsonNode? node, string key) => Text(node is JsonObject o ? o[key] : null);
    public static bool True(this JsonNode? node, string key) => node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    public static bool False(this JsonNode? node, string key) => node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && !b;
    public static JsonArray Array(this JsonNode? node, string key) => (node as JsonObject)?[key] as JsonArray ?? [];
    public static JsonObject Copy(this JsonObject node) => (JsonObject)node.DeepClone();
    public static JsonArray List(IEnumerable<JsonNode?> nodes) => new(nodes.Select(n => n?.DeepClone()).ToArray());
    public static string Trim(string s) => ForwardHost.TrimWhitespace(s);
    public static string Sanitize(string? text, int max = 300, bool ellipsis = false)
    {
        var clean = Regex.Replace(Regex.Replace(text ?? "", "[\\u0000-\\u001F\\u007F\\u2028\\u2029]", " "),
            "[\\u0009-\\u000D\\u0020\\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000\\uFEFF]+", " ").Trim(' ');
        return clean.Length <= max ? clean : clean[..(ellipsis ? max - 1 : max)].TrimEnd(' ') + (ellipsis ? "…" : "");
    }
    public static int? Port(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var text) && !Regex.IsMatch(text, "^[0-9]{1,5}$")) return null;
        if (v.TryGetValue<bool>(out _)) return null;
        return double.TryParse(Text(node), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && d == Math.Truncate(d) && d is > 0 and <= 65535 ? (int)d : null;
    }
    public static JsonObject? Parse(string value)
    { try { return JsonNode.Parse(value) as JsonObject; } catch (JsonException) { return null; } }
}
