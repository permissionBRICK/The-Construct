using System.Text.Json.Nodes;
namespace Construct.Companion.Core.Abstractions;

// Public release metadata over HTTPS with normal CA validation. No credentials.
// A 404 is { "notFound": true }; transport failures are null.
public interface IUpdateSource
{
    Task<JsonNode?> GetJsonAsync(Uri url, CancellationToken cancellationToken = default);
}
