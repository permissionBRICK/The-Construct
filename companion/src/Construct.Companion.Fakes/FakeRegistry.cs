using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Fakes;

public sealed class FakeRegistry : IRegistry
{
    private readonly Dictionary<string, Dictionary<string, string>> keys = new(StringComparer.OrdinalIgnoreCase);
    public string? ReadString(string key, string? name) => keys.TryGetValue(key, out var values) ? values.GetValueOrDefault(name ?? "") : null;
    public int? ReadInt32(string key, string? name) => int.TryParse(ReadString(key, name), out var value) ? value : null;
    public void WriteString(string key, string? name, string value)
    {
        if (!keys.TryGetValue(key, out var values)) keys[key] = values = new(StringComparer.OrdinalIgnoreCase);
        values[name ?? ""] = value;
    }
    public void DeleteValue(string key, string? name) { if (keys.TryGetValue(key, out var values)) values.Remove(name ?? ""); }
}
