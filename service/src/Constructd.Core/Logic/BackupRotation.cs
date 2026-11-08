using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

/// <summary>
/// Which complete backups of one <em>(owner, VM)</em> rotation removes (docs/plans/host-config-backups.md §3.6).
/// Pinned backups are never removed and do not count. Of the rest, the union of these slots stays:
/// <list type="bullet">
/// <item><c>keep</c> 1: the newest;</item>
/// <item><c>keep</c> 2: the newest and the newest created before the start of the current week;</item>
/// <item><c>keep</c> ≥ 3: the <c>keep − 2</c> newest, the newest before the week start and the newest
/// before the month start.</item>
/// </list>
/// Weeks start Monday 00:00 UTC, months on the 1st 00:00 UTC. When slots coincide, the next newest
/// backups fill the free places, so <c>keep</c> backups remain whenever that many exist.
/// </summary>
public static class BackupRotation
{
    public static DateTimeOffset WeekStart(DateTimeOffset now)
    {
        var day = now.UtcDateTime.Date;
        var sinceMonday = ((int)day.DayOfWeek + 6) % 7;
        return new DateTimeOffset(day.AddDays(-sinceMonday), TimeSpan.Zero);
    }

    public static DateTimeOffset MonthStart(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>The ids rotation deletes, newest first. Open sessions are never candidates.</summary>
    public static IReadOnlyList<string> Expired(IEnumerable<ConfigBackup> backups, int keep, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(backups);
        keep = Math.Max(1, keep);
        var candidates = backups.Where(b => b.IsComplete && !b.IsPinned(now))
            .OrderByDescending(b => b.CreatedAt).ThenByDescending(b => b.Id, StringComparer.Ordinal).ToList();
        var kept = new HashSet<string>(candidates.Take(keep >= 3 ? keep - 2 : 1).Select(b => b.Id), StringComparer.Ordinal);
        if (keep >= 2 && candidates.FirstOrDefault(b => b.CreatedAt < WeekStart(now)) is { } week) kept.Add(week.Id);
        if (keep >= 3 && candidates.FirstOrDefault(b => b.CreatedAt < MonthStart(now)) is { } month) kept.Add(month.Id);
        foreach (var next in candidates)
        {
            if (kept.Count >= keep) break;
            kept.Add(next.Id);
        }
        return candidates.Where(b => !kept.Contains(b.Id)).Select(b => b.Id).ToList();
    }
}
