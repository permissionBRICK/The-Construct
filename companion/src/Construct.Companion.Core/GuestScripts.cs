using System.Text.RegularExpressions;

namespace Construct.Companion.Core;

public static partial class GuestScripts
{
    private static readonly IReadOnlyDictionary<string, string> Templates = Load();
    public static IReadOnlyCollection<string> Names => Templates.Keys.ToArray();

    public static string Render(string name, IReadOnlyDictionary<string, string>? values = null)
    {
        if (!Templates.TryGetValue(name, out var template)) throw new ArgumentException("Unknown guest script.", nameof(name));
        return Fill(template, values);
    }

    // One pass, so a value that itself contains {{markers}} is never interpreted again.
    internal static string Fill(string template, IReadOnlyDictionary<string, string>? values) => Placeholder().Replace(template, match =>
        values is not null && values.TryGetValue(match.Groups[1].Value, out var value)
            ? value : throw new ArgumentException("Missing guest script value: " + match.Groups[1].Value, nameof(values)));

    // Embedded *.sh templates under one logical-name prefix, keyed by file stem.
    internal static Dictionary<string, string> LoadResources(string prefix)
    {
        var assembly = typeof(GuestScripts).Assembly;
        return assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(name => name[prefix.Length..^3], name =>
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
                return reader.ReadToEnd();
            }, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> Load()
    {
        var templates = LoadResources("GuestScripts.");
        templates["usage"] = templates["usage"].Replace("# construct:usage-collect", templates["usage-collect"], StringComparison.Ordinal);
        return templates;
    }

    [GeneratedRegex(@"\{\{([A-Za-z][A-Za-z0-9]*)\}\}")]
    private static partial Regex Placeholder();
}
