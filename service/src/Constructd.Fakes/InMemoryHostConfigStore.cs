using System.Text.Json;
using System.Text.Json.Serialization;
using Constructd.Core.Abstractions;
namespace Constructd.Fakes;

public sealed class InMemoryHostConfigStore(IClock clock) : IHostConfigStore, IHostConfigMetadata
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HostConfigSection> _sections = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) } };
    public Task<T?> GetAsync<T>(string section, CancellationToken ct) where T : class
    { lock (_gate) return Task.FromResult(_sections.TryGetValue(section, out var s) ? JsonSerializer.Deserialize<T>(s.ValueJson, Json) : null); }
    public Task SetAsync<T>(string section, T value, string updatedBy, CancellationToken ct) where T : class
    { lock (_gate) Write(new(section, JsonSerializer.Serialize(value, Json), clock.UtcNow, updatedBy)); return Task.CompletedTask; }
    public Task<bool> TrySetAsync<T>(string section, T value, string updatedBy, DateTimeOffset? expectedUpdatedAt, CancellationToken ct) where T : class =>
        TrySetSectionsAsync([new(section, JsonSerializer.Serialize(value, Json), clock.UtcNow, updatedBy)], new Dictionary<string, DateTimeOffset?> { [section] = expectedUpdatedAt }, ct);
    public Task<IReadOnlyList<HostConfigSection>> ListSectionsAsync(CancellationToken ct)
    { lock (_gate) return Task.FromResult<IReadOnlyList<HostConfigSection>>(_sections.Values.ToArray()); }
    public Task<bool> TrySetSectionsAsync(IReadOnlyList<HostConfigSection> sections, IReadOnlyDictionary<string, DateTimeOffset?> expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (expected.Any(x => _sections.GetValueOrDefault(x.Key)?.UpdatedAt != x.Value)) return Task.FromResult(false);
            foreach (var section in sections) Write(section);
            return Task.FromResult(true);
        }
    }
    private void Write(HostConfigSection value)
    {
        if (_sections.TryGetValue(value.Section, out var old) && value.UpdatedAt <= old.UpdatedAt) value = value with { UpdatedAt = old.UpdatedAt.AddTicks(1) };
        _sections[value.Section] = value;
    }
}
