using System.Text.RegularExpressions;

namespace Constructd.Core.Logic;

/// <summary>
/// VM names double as SSH aliases, key file names and config-sync branch suffixes (plan §4.3), so
/// they are restricted to lowercase DNS-label characters.
/// </summary>
public static partial class VmNameValidator
{
    public const string Pattern = "^[a-z0-9][a-z0-9-]{0,39}$";

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    public static bool IsValid(string? name) => !string.IsNullOrEmpty(name) && NameRegex().IsMatch(name);
}
