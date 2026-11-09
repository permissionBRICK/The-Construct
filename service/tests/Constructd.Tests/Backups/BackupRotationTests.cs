using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Tests.Backups;

/// <summary>Rotation (docs/plans/host-config-backups.md §3.6): newest slots, week and month slots, pins.</summary>
public sealed class BackupRotationTests
{
    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    private static ConfigBackup Complete(string id, string created, string? pinnedUntil = null) =>
        new(id, "alice", "dev", BackupTriggers.Scheduled, BackupStates.Complete, At(created), At(created), 10, new string('a', 64),
            pinnedUntil is null ? null : At(pinnedUntil));

    private static string[] Kept(IReadOnlyList<ConfigBackup> backups, int keep, string now)
    {
        var expired = BackupRotation.Expired(backups, keep, At(now));
        return backups.Select(b => b.Id).Except(expired).Order(StringComparer.Ordinal).ToArray();
    }

    [Theory]
    [InlineData("2026-11-08T23:59:59Z", "2026-11-02T00:00:00Z")] // Sunday: the week began on Monday
    [InlineData("2026-11-09T00:00:00Z", "2026-11-09T00:00:00Z")] // Monday 00:00 starts a new week
    [InlineData("2026-11-04T12:00:00+05:00", "2026-11-02T00:00:00Z")] // read in UTC
    [InlineData("2026-11-02T03:00:00+05:00", "2026-10-26T00:00:00Z")] // still Sunday in UTC
    public void Weeks_start_on_monday_utc(string now, string expected) =>
        Assert.Equal(At(expected), BackupRotation.WeekStart(At(now)));

    [Theory]
    [InlineData("2026-11-08T10:00:00Z", "2026-11-01T00:00:00Z")]
    [InlineData("2026-11-01T00:00:00Z", "2026-11-01T00:00:00Z")]
    [InlineData("2026-11-01T01:00:00+02:00", "2026-10-01T00:00:00Z")]
    public void Months_start_on_the_first_utc(string now, string expected) =>
        Assert.Equal(At(expected), BackupRotation.MonthStart(At(now)));

    // Daily backups from 2026-10-20 to 2026-11-08 (a Sunday); the week began 2026-11-02, the month 2026-11-01.
    private static readonly ConfigBackup[] Daily = Enumerable.Range(0, 20)
        .Select(i => Complete($"d{i:00}", At("2026-10-20T03:00:00Z").AddDays(i).ToString("O"))).ToArray();

    [Fact]
    public void Keep_one_leaves_the_newest() =>
        Assert.Equal(["d19"], Kept(Daily, 1, "2026-11-08T12:00:00Z"));

    [Fact]
    public void Keep_two_adds_the_newest_before_the_week() =>
        // d12 = 2026-11-01, the last one before Monday 2026-11-02.
        Assert.Equal(["d12", "d19"], Kept(Daily, 2, "2026-11-08T12:00:00Z"));

    [Fact]
    public void Keep_three_adds_the_newest_before_the_month() =>
        // d11 = 2026-10-31, the last one before 2026-11-01.
        Assert.Equal(["d11", "d12", "d19"], Kept(Daily, 3, "2026-11-08T12:00:00Z"));

    [Fact]
    public void Keep_four_keeps_two_newest_and_both_older_slots() =>
        Assert.Equal(["d11", "d12", "d18", "d19"], Kept(Daily, 4, "2026-11-08T12:00:00Z"));

    [Fact]
    public void Keep_ten_keeps_eight_newest_and_both_older_slots() =>
        // The eight newest are d12..d19; d12 is also the week slot, so the next newest, d10, fills the tenth place.
        Assert.Equal(["d10", "d11", "d12", "d13", "d14", "d15", "d16", "d17", "d18", "d19"], Kept(Daily, 10, "2026-11-08T12:00:00Z"));

    [Fact]
    public void The_week_slot_moves_on_at_the_monday_boundary()
    {
        // Just before Monday 2026-11-09 00:00 the week slot is d12; at the boundary it becomes the newest before it,
        // d19, which is also the newest, so the next newest fills the second place.
        Assert.Equal(["d12", "d19"], Kept(Daily, 2, "2026-11-08T23:59:59Z"));
        Assert.Equal(["d18", "d19"], Kept(Daily, 2, "2026-11-09T00:00:00Z"));
    }

    [Fact]
    public void The_month_slot_moves_on_at_the_first_of_the_month()
    {
        Assert.Equal(["d11", "d12", "d19"], Kept(Daily, 3, "2026-11-08T12:00:00Z"));
        // On 2026-12-01 the newest before the month and before the week (Monday 2026-11-30) are both d19;
        // the next newest fill the free places.
        Assert.Equal(["d17", "d18", "d19"], Kept(Daily, 3, "2026-12-01T00:00:00Z"));
    }

    [Fact]
    public void Coinciding_slots_are_filled_with_the_next_newest()
    {
        // Two backups, both before the week and the month: the newest fills all three slots, the other one the rest.
        var old = new[] { Complete("a", "2026-10-01T00:00:00Z"), Complete("b", "2026-10-02T00:00:00Z") };
        Assert.Equal(["a", "b"], Kept(old, 3, "2026-11-08T12:00:00Z"));
        Assert.Equal(["a", "b"], Kept(old, 4, "2026-11-08T12:00:00Z"));
        Assert.Empty(BackupRotation.Expired([Complete("only", "2026-11-08T01:00:00Z")], 1, At("2026-11-08T12:00:00Z")));
    }

    [Fact]
    public void Pinned_backups_are_never_deleted_and_do_not_count()
    {
        var backups = Daily.Append(Complete("pinned", "2026-11-08T06:00:00Z", pinnedUntil: "2026-11-20T00:00:00Z")).ToArray();
        // The pinned one is the newest, yet d19 still fills the newest slot.
        Assert.Equal(["d19", "pinned"], Kept(backups, 1, "2026-11-08T12:00:00Z"));
        // Once the pin has expired it counts like any other backup, and it is the newest.
        Assert.Equal(["pinned"], Kept(backups, 1, "2026-11-21T00:00:00Z"));
    }

    [Fact]
    public void Open_sessions_are_not_candidates()
    {
        var open = new ConfigBackup("open", "alice", "dev", BackupTriggers.Manual, BackupStates.Open, At("2026-11-08T11:00:00Z"));
        Assert.DoesNotContain("open", BackupRotation.Expired([.. Daily, open], 1, At("2026-11-08T12:00:00Z")));
    }
}
