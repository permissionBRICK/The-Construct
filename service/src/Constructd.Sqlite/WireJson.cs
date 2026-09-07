using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite;

internal static class WireJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) } };
    internal static string Enum<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
    internal static string? Serialize<T>(T? value) => value is null ? null : JsonSerializer.Serialize(value, Options);
    internal static T? Read<T>(string? value) => value is null ? default : JsonSerializer.Deserialize<T>(value, Options);
    internal static long GetLong(this SqliteDataReader r, string c) => r.GetInt64(r.GetOrdinal(c));
    internal static long? GetLongOrNull(this SqliteDataReader r, string c) => r.IsDBNull(r.GetOrdinal(c)) ? null : r.GetLong(c);
    internal static bool? GetBoolOrNull(this SqliteDataReader r, string c) => r.GetIntOrNull(c) is int n ? n != 0 : null;
    internal static DateTimeOffset? GetTime(this SqliteDataReader r, string c) => SqliteDatabase.ReadTimeOrNull(r.GetStringOrNull(c));
    internal static T GetEnum<T>(this SqliteDataReader r, string c) where T : struct, Enum => SqliteDatabase.ReadEnum<T>(r.GetString(c));
}
