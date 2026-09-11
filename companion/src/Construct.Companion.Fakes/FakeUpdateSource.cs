using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Fakes;
public sealed class FakeUpdateSource : IUpdateSource
{
    public List<Uri> Requests { get; } = [];
    public Queue<JsonNode?> Responses { get; } = new();
    public Task<JsonNode?> GetJsonAsync(Uri url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Requests.Add(url); return Task.FromResult(Responses.Dequeue()?.DeepClone());
    }
}
