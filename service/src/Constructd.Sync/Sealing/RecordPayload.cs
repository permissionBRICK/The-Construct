using System.Text.Json.Nodes;

namespace Constructd.Sync.Sealing;

/// <summary>A write of a payload newer than this participant understands (9.8).</summary>
public sealed class PayloadTooNewException(int version, int supported)
    : Exception($"This record was written with payload version {version}; this participant understands up to {supported}. Update this device.")
{
    public int Version { get; } = version;
}

/// <summary>
/// The payload inside the ciphertext (9.8): <c>payloadVersion</c>, the record's <c>value</c>, the
/// <c>origins</c> that contributed to it, and whatever fields a newer participant added, which a
/// rewrite keeps as they are.
/// </summary>
public static class RecordPayload
{
    /// <summary>The payload version this build writes.</summary>
    public const int CurrentVersion = 1;

    public static int VersionOf(JsonObject? payload) =>
        payload?["payloadVersion"] is JsonValue v && v.TryGetValue<int>(out var version) ? version : 0;

    /// <summary>
    /// The payload for a new write over <paramref name="previous"/>: its unknown fields kept, value
    /// and origins replaced. Refused when the previous payload is newer than <paramref name="supported"/>.
    /// </summary>
    public static JsonObject Rewrite(JsonObject? previous, JsonNode? value, IEnumerable<string> origins, int supported = CurrentVersion)
    {
        var version = VersionOf(previous);
        if (version > supported) throw new PayloadTooNewException(version, supported);
        var payload = previous?.DeepClone().AsObject() ?? new JsonObject();
        payload["payloadVersion"] = supported;
        payload["value"] = value?.DeepClone();
        payload["origins"] = new JsonArray(origins.Distinct(StringComparer.Ordinal).Select(o => (JsonNode)JsonValue.Create(o)!).ToArray());
        return payload;
    }

    public static JsonNode? ValueOf(JsonObject? payload) => payload?["value"];

    public static IReadOnlyList<string> OriginsOf(JsonObject? payload) =>
        payload?["origins"] is JsonArray origins ? origins.Select(o => o?.GetValue<string>() ?? "").Where(o => o.Length > 0).ToList() : [];
}
