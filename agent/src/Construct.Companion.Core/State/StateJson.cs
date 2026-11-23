using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Core.State;

public static class StateJson
{
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string String(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject => "[object Object]",
        JsonArray array => string.Join(",", array.Select(v => v is null ? "" : String(v))),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => NumberString(Number(v)!.Value),
        _ => value.ToJsonString(Compact)
    };
    public static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public static bool? Boolean(JsonNode? value) => value is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    public static double? Number(JsonNode? value) => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.TryGetValue<double>(out var n) ? n : double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture) : null;
    public static string StripBom(string text) => text.StartsWith('\ufeff') ? text[1..] : text;
    public static bool StrictEquals(JsonNode? a, JsonNode? b) => a is JsonObject or JsonArray || b is JsonObject or JsonArray ? ReferenceEquals(a, b) : JsonNode.DeepEquals(a, b);
    public static bool Truthy(JsonNode? value) => value switch
    {
        null => false,
        JsonValue v when Boolean(v) is bool b => b,
        JsonValue v when Number(v) is {} n => n != 0 && !double.IsNaN(n),
        JsonValue v when Text(v) is {} s => s.Length > 0,
        _ => true
    };
    public static double CoerceNumber(JsonNode? value)
    {
        if (value is null) return 0;
        if (Boolean(value) is bool b) return b ? 1 : 0;
        if (Number(value) is {} n) return n;
        var text = Trim(String(value)); if (text.Length == 0) return 0;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) return hex;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : double.NaN;
    }
    public static string Trim(string value) => value.Trim(' ', '\t', '\r', '\n', '\v', '\f', '\u00a0', '\u1680', '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200a', '\u2028', '\u2029', '\u202f', '\u205f', '\u3000', '\ufeff');
    public static string? Nonempty(JsonNode? node) { var s = Text(node); return s is null || Trim(s).Length == 0 ? null : Trim(s); }
    public static JsonObject Merge(JsonObject first, JsonObject second)
    {
        var result = (JsonObject)first.DeepClone();
        foreach (var (key, value) in second) result[key] = value?.DeepClone();
        return result;
    }
    public static JsonObject? ParseObject(string? text)
    {
        try { return JsonNode.Parse(StripBom(text ?? "")) as JsonObject; }
        catch (JsonException) { return null; }
    }
    public static JsonObject? ReadObject(IFileSystem fs, string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { var bytes = fs.ReadFile(path); return bytes is null ? null : ParseObject(Encoding.UTF8.GetString(bytes)); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
    public static byte[] Bytes(JsonNode value) => Encoding.UTF8.GetBytes(Stringify(value, true) + "\n");
    public static string Quote(JsonNode? node) => Stringify(node);
    public static string Stringify(JsonNode? node, bool pretty = false)
    {
        var output = new StringBuilder();
        void Quoted(string value)
        {
            output.Append('"');
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                switch (c)
                {
                    case '"': output.Append("\\\""); break;
                    case '\\': output.Append("\\\\"); break;
                    case '\b': output.Append("\\b"); break;
                    case '\f': output.Append("\\f"); break;
                    case '\n': output.Append("\\n"); break;
                    case '\r': output.Append("\\r"); break;
                    case '\t': output.Append("\\t"); break;
                    default:
                        if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { output.Append(c).Append(value[++i]); }
                        else if (c < 32 || char.IsSurrogate(c)) output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else output.Append(c);
                        break;
                }
            }
            output.Append('"');
        }
        void Write(JsonNode? value, int depth)
        {
            if (value is null) { output.Append("null"); return; }
            if (value is JsonValue scalar)
            {
                if (Text(scalar) is {} text) Quoted(text);
                else if (Boolean(scalar) is bool boolean) output.Append(boolean ? "true" : "false");
                else if (Number(scalar) is {} number) output.Append(NumberString(number));
                else output.Append(scalar.ToJsonString(Compact));
                return;
            }
            var entries = value is JsonObject obj ? obj.OrderBy(p => uint.TryParse(p.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < uint.MaxValue && index.ToString(CultureInfo.InvariantCulture) == p.Key ? (long)index : long.MaxValue).ToArray() : ((JsonArray)value).Select(v => new KeyValuePair<string, JsonNode?>("", v)).ToArray();
            var isObject = value is JsonObject; output.Append(isObject ? '{' : '[');
            for (var i = 0; i < entries.Length; i++)
            {
                if (i > 0) output.Append(',');
                if (pretty) output.Append('\n').Append(' ', (depth + 1) * 2);
                if (isObject) { Quoted(entries[i].Key); output.Append(pretty ? ": " : ":"); }
                Write(entries[i].Value, depth + 1);
            }
            if (pretty && entries.Length > 0) output.Append('\n').Append(' ', depth * 2);
            output.Append(isObject ? '}' : ']');
        }
        Write(node, 0); return output.ToString();
    }
    private static string NumberString(double number)
    {
        if (!double.IsFinite(number)) return "null";
        if (number == 0) return "0";
        var text = number.ToString("R", CultureInfo.InvariantCulture); var at = text.IndexOf('E');
        if (at < 0) return text;
        var exponent = int.Parse(text[(at + 1)..], CultureInfo.InvariantCulture);
        var coefficient = text[..at]; var negative = coefficient.StartsWith('-'); if (negative) coefficient = coefficient[1..];
        var dot = coefficient.IndexOf('.'); if (dot < 0) dot = coefficient.Length;
        var digits = coefficient.Replace(".", "", StringComparison.Ordinal); var position = dot + exponent;
        if (Math.Abs(number) >= 1e-6 && Math.Abs(number) < 1e21)
        {
            var expanded = position <= 0 ? "0." + new string('0', -position) + digits : position >= digits.Length ? digits + new string('0', position - digits.Length) : digits.Insert(position, ".");
            return (negative ? "-" : "") + expanded;
        }
        return (negative ? "-" : "") + coefficient + "e" + (exponent >= 0 ? "+" : "") + exponent.ToString(CultureInfo.InvariantCulture);
    }
}
