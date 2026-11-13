using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Tests.Backups;

/// <summary>
/// The retention rule inside rotation (docs/plans/construct-platform-design.md 10.2, Appendix C.6): whatever
/// <c>keep</c> says, the newest complete host-requested backup at least <c>minHorizonDays</c> old stays, and
/// until one is that old, the oldest. Each test runs rotation day by day, as the host does after every upload.
/// </summary>
public sealed class BackupRetentionTests
{
    private const int Horizon = 30;
    private static readonly DateTimeOffset Day0 = new(2026, 10, 1, 3, 0, 0, TimeSpan.Zero);

    private static ConfigBackup Host(string id, DateTimeOffset created, string trigger = BackupTriggers.Scheduled, string? origin = BackupOrigins.Host) =>
        new(id, "alice", "dev", trigger, BackupStates.Complete, created, created, 10, new string('a', 64), Origin: origin);

    private static ConfigBackup Slot(string id, DateTimeOffset created) =>
        new(id, "alice", "dev", BackupTriggers.Slot, BackupStates.Complete, created, created, 10, new string('a', 64), Origin: BackupOrigins.Slot);

    /// <summary>Adds the day's backup (when there is one) and applies rotation, as the upload does.</summary>
    private static void Upload(List<ConfigBackup> stored, ConfigBackup? added, int keep, DateTimeOffset now)
    {
        if (added is not null) stored.Add(added);
        var expired = BackupRotation.Expired(stored, keep, now, Horizon).ToHashSet(StringComparer.Ordinal);
        stored.RemoveAll(b => expired.Contains(b.Id));
    }

    [Fact]
    public void With_keep_one_and_daily_uploads_from_day_one_a_backup_reaches_the_horizon_and_stays()
    {
        var stored = new List<ConfigBackup>();
        for (var day = 0; day < 90; day++)
        {
            var now = Day0.AddDays(day);
            Upload(stored, Host($"d{day:00}", now), keep: 1, now);
            // Before any backup is 30 days old the first one stays; from then on one at least 30 days old does.
            if (day < Horizon) Assert.Contains(stored, b => b.Id == "d00");
            else Assert.Contains(stored, b => now - b.CreatedAt >= TimeSpan.FromDays(Horizon));
            Assert.Contains(stored, b => b.Id == $"d{day:00}");
            // keep 1 plus the protected one: never more.
            Assert.True(stored.Count <= 2, $"day {day}: {stored.Count}");
        }
    }

    [Fact]
    public void Gaps_in_the_schedule_do_not_release_the_protected_backup()
    {
        var stored = new List<ConfigBackup>();
        for (var day = 0; day < 10; day++) Upload(stored, Host($"a{day:00}", Day0.AddDays(day)), keep: 1, Day0.AddDays(day));
        // Nothing for 40 days, then daily again. a09 is the newest backup older than the horizon and stays
        // until a newer one is that old too, 30 days after the schedule resumed.
        for (var day = 50; day < 79; day++)
        {
            var now = Day0.AddDays(day);
            Upload(stored, Host($"b{day:00}", now), keep: 1, now);
            Assert.Contains(stored, b => b.Id == "a09");
        }
        // Hourly rotation during a gap removes nothing it protects either: the oldest stays until the newer one
        // is that old itself, and then that one stays.
        var quiet = new List<ConfigBackup> { Host("x", Day0), Host("y", Day0.AddDays(1)) };
        for (var hour = 0; hour < 24 * 60; hour++)
        {
            var now = Day0.AddDays(1).AddHours(hour);
            Upload(quiet, null, keep: 1, now);
            Assert.Contains(quiet, b => b.Id == "y");
            if (now < Day0.AddDays(1 + Horizon)) Assert.Contains(quiet, b => b.Id == "x");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Daily_junk_across_a_month_boundary_never_removes_the_last_good_backup_within_the_horizon(int keep)
    {
        // Good daily backups through 2026-10-20, then a taken-over VM answers every request with junk,
        // across the start of November (and of a week), for as long as the horizon.
        var stored = new List<ConfigBackup>();
        var junkStart = new DateTimeOffset(2026, 10, 21, 3, 0, 0, TimeSpan.Zero);
        for (var day = Day0; day < junkStart; day = day.AddDays(1)) Upload(stored, Host("good-" + day.ToString("MMdd"), day), keep, day);
        for (var i = 0; i < Horizon; i++)
        {
            var now = junkStart.AddDays(i);
            Upload(stored, Host($"junk-{i:00}", now), keep, now);
            Assert.Contains(stored, b => b.Id.StartsWith("good-", StringComparison.Ordinal));
        }
        Assert.True(junkStart.AddDays(Horizon - 1) > new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Slot_uploads_neither_rotate_nor_count_and_are_never_protected()
    {
        var now = Day0.AddDays(40);
        var backups = new[] { Host("old", Day0), Host("new", now.AddHours(-1)), Slot("slot", Day0.AddDays(-5)) };
        Assert.Equal("old", BackupRotation.Protected(backups, now, Horizon)!.Id);
        Assert.Empty(BackupRotation.Expired(backups, 1, now, Horizon));
        // Only slots: nothing is protected and rotation leaves them to the slot swap.
        Assert.Null(BackupRotation.Protected([Slot("s", Day0)], now, Horizon));
        Assert.Empty(BackupRotation.Expired([Slot("s1", Day0), Slot("s2", Day0.AddDays(1))], 1, now, Horizon));
    }

    [Fact]
    public void A_backup_stored_before_the_mark_counts_as_host_requested()
    {
        var now = Day0.AddDays(10);
        var legacy = Host("legacy", Day0, BackupTriggers.PreReinstall, origin: null);
        Assert.Equal("legacy", BackupRotation.Protected([legacy, Host("newer", Day0.AddDays(2))], now, Horizon)!.Id);
        Assert.Equal(["mid"], BackupRotation.Expired([legacy, Host("mid", Day0.AddDays(1)), Host("newer", Day0.AddDays(2))], 1, now, Horizon));
    }

    [Fact]
    public void The_protected_backup_stays_whatever_keep_says_and_may_keep_more()
    {
        var now = Day0.AddDays(45);
        var backups = Enumerable.Range(0, 46).Select(d => Host($"d{d:00}", Day0.AddDays(d))).ToArray();
        // d15 is the newest at least 30 days old; keep 1 keeps the newest as well.
        Assert.Equal("d15", BackupRotation.Protected(backups, now, Horizon)!.Id);
        Assert.Equal(["d15", "d45"], backups.Select(b => b.Id).Except(BackupRotation.Expired(backups, 1, now, Horizon)).Order(StringComparer.Ordinal));
    }
}
