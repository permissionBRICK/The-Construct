using System.Globalization;
using Constructd.Core.Domain;
namespace Constructd.Core.Logic;

public static class TokenUsageMath
{
    public static bool ValidWindow(string window) => window is "today" or "month" or "all";
    public static bool ValidDay(string? day) => day is not null &&
        (day.Length == 10 && DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
         day.Length == 7 && DateOnly.TryParseExact(day + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));

    public static DateOnly PeriodEnd(string day)
    {
        var date = DateOnly.ParseExact(day.Length == 7 ? day + "-01" : day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return day.Length == 7 ? new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month)) : date;
    }

    public static TokenUsageSummary Aggregate(IEnumerable<TokenUsageRow> source, string window, DateTimeOffset now)
    {
        if (!ValidWindow(window)) throw new ArgumentException("Unknown usage window.", nameof(window));
        var rows = source.ToArray();
        var today = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var month = today[..7];
        // Resolve precedence before filtering the requested window. A monthly row never counts today.
        var dailyMonths = rows.Where(r => r.Usage.Day.Length == 10)
            .Select(r => (r.Vm.ToUpperInvariant(), r.Usage.Tool, r.Usage.Day[..7])).ToHashSet();
        var selected = rows.Where(r => (r.Usage.Day.Length == 10 ||
                !dailyMonths.Contains((r.Vm.ToUpperInvariant(), r.Usage.Tool, r.Usage.Day))) &&
            (window == "all" || window == "today" && r.Usage.Day == today ||
             window == "month" && r.Usage.Day.StartsWith(month, StringComparison.Ordinal))).ToArray();
        TokenUsageTotals Sum(IEnumerable<TokenUsageRow> group) => new(group.Sum(r => (decimal)r.Usage.TotalTokens), group.Sum(r => (decimal)r.Usage.CostUsdMicros) / 1_000_000m);
        IReadOnlyList<TokenUsageTool> Tools(IEnumerable<TokenUsageRow> group) => group.GroupBy(r => r.Usage.Tool)
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => { var s = Sum(g); return new TokenUsageTool(g.Key, s.Tokens, s.CostUsd); }).ToArray();
        // Keep zero-usage VMs in a window so their last report is still visible.
        var byVm = rows.GroupBy(r => (r.Vm.ToUpperInvariant(), r.Owner.ToUpperInvariant())).Select(g =>
        {
            var latest = g.MaxBy(r => r.ReportedAt)!;
            var included = selected.Where(r => Ownership.SameName(r.Vm, latest.Vm) && Ownership.SameName(r.Owner, latest.Owner)).ToArray();
            var sum = Sum(included);
            return new TokenUsageVm(latest.Vm, latest.Owner, latest.VmDeletedAt is not null, sum.Tokens, sum.CostUsd, g.Max(r => r.ReportedAt), Tools(included));
        }).OrderBy(v => v.Vm, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.User, StringComparer.OrdinalIgnoreCase).ToArray();
        var byUser = byVm.GroupBy(v => v.User, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TokenUsageUser(g.Key, g.Sum(v => v.Tokens), g.Sum(v => v.CostUsd), g.Count(), g.Max(v => v.LastReportedAt),
                Tools(selected.Where(r => Ownership.SameName(r.Owner, g.Key))))).ToArray();
        return new(window, now, Sum(selected), byUser, byVm);
    }
}
