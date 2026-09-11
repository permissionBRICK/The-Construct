using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Fakes;

public sealed class FakeRegistry : IRegistry
{
    private readonly Dictionary<string, Dictionary<string, string>> keys = new(StringComparer.OrdinalIgnoreCase);
    public string? ReadString(string key, string? name) => keys.TryGetValue(key, out var values) ? values.GetValueOrDefault(name ?? "") : null;
    public void WriteString(string key, string? name, string value)
    {
        if (!keys.TryGetValue(key, out var values)) keys[key] = values = new(StringComparer.OrdinalIgnoreCase);
        values[name ?? ""] = value;
    }
    public void DeleteValue(string key, string? name) { if (keys.TryGetValue(key, out var values)) values.Remove(name ?? ""); }
    public void DeleteTree(string key)
    {
        foreach (var path in keys.Keys.Where(p => p.Equals(key, StringComparison.OrdinalIgnoreCase) || p.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)).ToArray()) keys.Remove(path);
    }
}
