using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Constructd.Core.Logic;

public static class OperationFingerprint
{
    public static bool ValidKey(string key) => Regex.IsMatch(key, @"\A[A-Za-z0-9._:-]{8,128}\z", RegexOptions.CultureInvariant);
    public static string Compute(string route, JsonElement body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer, body);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(route + "\n" + Encoding.UTF8.GetString(buffer.ToArray()))));
    }
    public static string ChildName(string parent, string owner, string? key)
    {
        var suffix = key is null ? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2)) :
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(owner + ":" + key)))[..4];
        return parent[..Math.Min(parent.Length, 58)].TrimEnd('-') + "-" + suffix;
    }
    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var p in value.EnumerateObject().Where(p => p.Name != "operationKey").OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(p.Name); Write(writer, p.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); break;
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()!.Normalize(NormalizationForm.FormC)); break;
            default: value.WriteTo(writer); break;
        }
    }
}
