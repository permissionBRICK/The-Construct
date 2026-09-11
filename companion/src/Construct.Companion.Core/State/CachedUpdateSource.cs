using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Core.State;

public static class RefreshCachePolicy
{
    public static bool Fresh(string area, bool success, double ageMilliseconds) =>
        ageMilliseconds < (success ? area == "usage" ? 300000 : 600000 : 60000);
}

// URLs encode the JS cache identities: Construct repo/ref/installed commit and
// agent id/channel. One instance shares each upstream lookup with every other VM.
public sealed class CachedUpdateSource(IUpdateSource source, IClock clock) : IUpdateSource
{
    private sealed class Entry
    {
        public SemaphoreSlim Serial { get; } = new(1,1);
        public DateTimeOffset? At;
        public JsonNode? Value;
    }
    private readonly ConcurrentDictionary<string,Entry> cache = new(StringComparer.Ordinal);
    public async Task<JsonNode?> GetJsonAsync(Uri url, CancellationToken cancellationToken = default)
    {
        var entry = cache.GetOrAdd(url.AbsoluteUri, _ => new());
        await entry.Serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = clock.UtcNow;
            var success = entry.Value is not null && !(entry.Value is JsonObject value && StateJson.Boolean(value["notFound"]) == true);
            if (entry.At is {} at && RefreshCachePolicy.Fresh("updates",success,(now-at).TotalMilliseconds)) return entry.Value?.DeepClone();
            entry.Value = (await source.GetJsonAsync(url,cancellationToken).ConfigureAwait(false))?.DeepClone();
            entry.At = now; return entry.Value?.DeepClone();
        }
        finally { entry.Serial.Release(); }
    }
}
