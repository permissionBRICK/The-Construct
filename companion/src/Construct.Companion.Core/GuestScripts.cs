using System.Text.RegularExpressions;

namespace Construct.Companion.Core;

public static partial class GuestScripts
{
    private static readonly IReadOnlyDictionary<string, string> Templates = Load();
    public static IReadOnlyCollection<string> Names => Templates.Keys.ToArray();

    public static string Render(string name, IReadOnlyDictionary<string, string>? values = null)
    {
        if (!Templates.TryGetValue(name, out var template)) throw new ArgumentException("Unknown guest script.", nameof(name));
        return Placeholder().Replace(template, match =>
            values is not null && values.TryGetValue(match.Groups[1].Value, out var value)
                ? value : throw new ArgumentException("Missing guest script value: " + match.Groups[1].Value, nameof(values)));
    }

    private static Dictionary<string, string> Load()
    {
        var assembly = typeof(GuestScripts).Assembly;
        return assembly.GetManifestResourceNames().Where(name => name.StartsWith("GuestScripts.", StringComparison.Ordinal))
            .ToDictionary(name => name[13..^3], name =>
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
                return reader.ReadToEnd();
            }, StringComparer.Ordinal);
    }

    [GeneratedRegex(@"\{\{([A-Za-z][A-Za-z0-9]*)\}\}")]
    private static partial Regex Placeholder();
}
