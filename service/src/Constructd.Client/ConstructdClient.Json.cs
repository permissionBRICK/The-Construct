using System.Text.Json;
using System.Text.Json.Serialization;

namespace Constructd.Client;

/// <summary>
/// The one hand-written part: the service writes every enum as a camelCase string (ApiJson), and
/// NSwag puts a string converter only on single enum properties, not on lists of them.
/// </summary>
public partial class ConstructdClient
{
    static partial void UpdateJsonSerializerSettings(JsonSerializerOptions settings) =>
        settings.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
}
