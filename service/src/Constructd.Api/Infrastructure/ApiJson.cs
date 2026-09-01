using System.Text.Json;
using System.Text.Json.Serialization;

namespace Constructd.Api.Infrastructure;

/// <summary>
/// The single JSON contract of the API: camelCase properties, enums as camelCase strings
/// (<c>running</c>, <c>save</c>, <c>host</c>), used by the endpoints, the SSE writer and the tests
/// alike so the wire format cannot drift between them.
/// </summary>
public static class ApiJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
