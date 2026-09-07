using System.Text.RegularExpressions;
namespace Constructd.Core.Logic;

public static class LifetimeParser
{
    public static long? Parse(string input, DateTimeOffset now)
    {
        if (input == "never") return null;
        if (!Regex.IsMatch(input, @"\A[1-9][0-9]*[mhd]\z") || !long.TryParse(input[..^1], out var count))
            throw new ChildValidationException("validation", "lifetime");
        var factor = input[^1] switch { 'm' => 60L, 'h' => 3600L, _ => 86400L };
        if (count > (long)(DateTimeOffset.MaxValue - now).TotalSeconds / factor || count * factor < 300)
            throw new ChildValidationException("validation", "lifetime");
        return count * factor;
    }
}
