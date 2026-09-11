namespace Construct.Companion.Core;

public static class ShellQuote
{
    public static string Single(string? value) => "'" + (value ?? "").Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
