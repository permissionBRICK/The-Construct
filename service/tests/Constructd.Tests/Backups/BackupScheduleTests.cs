using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Tests.Backups;

/// <summary>When the heartbeat tells the guest to back up (docs/plans/host-config-backups.md §3.6 "Due").</summary>
public sealed class BackupScheduleTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly BackupsConfig Host = new();
    private static BackupVmState Vm(DateTimeOffset? success = null, DateTimeOffset? attempt = null, int? interval = null,
        DateTimeOffset? request = null) => new("alice", "dev", interval, null, attempt, success, null, null, request);

    private static BackupDecision Due(BackupVmState? vm, DateTimeOffset now, bool busy = false, bool open = false, BackupsConfig? host = null) =>
        BackupSchedule.Evaluate(host ?? Host, vm, open, busy, Created, now);

    [Fact]
    public void A_vm_without_a_successful_backup_is_due_at_once_when_idle()
    {
        Assert.Equal(new BackupDecision(true, "scheduled"), Due(null, Created.AddMinutes(5)));
        Assert.Equal(new BackupDecision(true, "scheduled"), Due(Vm(), Created.AddMinutes(5)));
    }

    [Fact]
    public void A_busy_vm_waits_for_an_idle_heartbeat_unless_it_has_been_due_for_six_hours()
    {
        Assert.False(Due(null, Created.AddHours(5).AddMinutes(59), busy: true).Due);
        Assert.Equal(new BackupDecision(true, "scheduled"), Due(null, Created.AddHours(6), busy: true));
        var success = Created.AddDays(1);
        Assert.False(Due(Vm(success, success), success.AddHours(24 + 5), busy: true).Due);
        Assert.True(Due(Vm(success, success), success.AddHours(24 + 6), busy: true).Due);
    }

    [Fact]
    public void The_interval_counts_from_the_last_success()
    {
        var success = Created.AddDays(2);
        Assert.False(Due(Vm(success, success), success.AddHours(23).AddMinutes(59)).Due);
        Assert.True(Due(Vm(success, success), success.AddHours(24)).Due);
        Assert.True(Due(Vm(success, success, interval: 2), success.AddHours(2)).Due);
    }

    [Fact]
    public void Retries_after_an_attempt_wait_for_the_interval_or_six_hours_whichever_is_shorter()
    {
        var success = Created.AddDays(2);
        var attempt = success.AddHours(30);
        // Due by success (24 h), but attempted 0 h ago: a retry waits min(24 h, 6 h).
        Assert.False(Due(Vm(success, attempt), attempt.AddHours(5)).Due);
        Assert.True(Due(Vm(success, attempt), attempt.AddHours(6)).Due);
        // With a 2 h interval the retry waits 2 h.
        Assert.False(Due(Vm(success, attempt, interval: 2), attempt.AddHours(1)).Due);
        Assert.True(Due(Vm(success, attempt, interval: 2), attempt.AddHours(2)).Due);
        // Failures without any success: retried every six hours.
        Assert.False(Due(Vm(attempt: Created.AddHours(1)), Created.AddHours(6)).Due);
        Assert.True(Due(Vm(attempt: Created.AddHours(1)), Created.AddHours(7)).Due);
    }

    [Fact]
    public void A_manual_request_is_due_at_once_even_when_busy_and_expires_after_an_hour()
    {
        var success = Created.AddDays(2);
        var request = success.AddMinutes(10);
        Assert.Equal(new BackupDecision(true, "manual"), Due(Vm(success, success, request: request), request.AddMinutes(1), busy: true));
        Assert.Equal(new BackupDecision(true, "manual"), Due(Vm(success, success, interval: 0, request: request), request.AddMinutes(59)));
        Assert.False(Due(Vm(success, success, request: request), request.AddHours(1)).Due);
        Assert.True(BackupSchedule.RequestPending(Vm(request: request), request.AddMinutes(59)));
        Assert.False(BackupSchedule.RequestPending(Vm(request: request), request.AddHours(1)));
    }

    [Fact]
    public void Nothing_is_due_while_a_session_is_open_or_backups_are_off()
    {
        var request = Created.AddMinutes(1);
        Assert.False(Due(Vm(request: request), request, open: true).Due);
        Assert.False(Due(Vm(request: request), request, host: Host with { Enabled = false }).Due);
        Assert.False(Due(null, Created.AddDays(3), host: Host with { Enabled = false }).Due);
    }

    [Fact]
    public void An_interval_of_zero_turns_scheduling_off_but_not_requests()
    {
        Assert.False(Due(Vm(interval: 0), Created.AddDays(30)).Due);
        Assert.Null(BackupSchedule.NextDueAt(Host, Vm(interval: 0), Created, Created.AddDays(30)));
        // The host's 0 wins over a VM override.
        var off = Host with { IntervalHours = 0 };
        Assert.Equal(0, BackupSchedule.EffectiveIntervalHours(off, Vm(interval: 12)));
        Assert.False(Due(Vm(interval: 12), Created.AddDays(30), host: off).Due);
        Assert.True(Due(Vm(interval: 12, request: Created.AddDays(30)), Created.AddDays(30), host: off).Due);
    }

    [Fact]
    public void Next_due_at_reports_the_scheduled_time_or_the_pending_request()
    {
        var success = Created.AddDays(2);
        Assert.Equal(Created, BackupSchedule.NextDueAt(Host, null, Created, Created.AddHours(1)));
        Assert.Equal(success.AddHours(24), BackupSchedule.NextDueAt(Host, Vm(success, success), Created, success));
        Assert.Equal(success.AddHours(36), BackupSchedule.NextDueAt(Host, Vm(success, success.AddHours(30)), Created, success));
        var request = success.AddMinutes(5);
        Assert.Equal(request, BackupSchedule.NextDueAt(Host, Vm(success, success, request: request), Created, request.AddMinutes(1)));
        Assert.Null(BackupSchedule.NextDueAt(Host with { Enabled = false }, null, Created, success));
    }

    [Fact]
    public void The_effective_keep_is_the_override_capped_by_the_host()
    {
        Assert.Equal(4, BackupSchedule.EffectiveKeep(Host, null));
        Assert.Equal(7, BackupSchedule.EffectiveKeep(Host, new("alice", "dev", Keep: 7)));
        Assert.Equal(3, BackupSchedule.EffectiveKeep(Host with { MaxKeep = 3, Keep = 2 }, new("alice", "dev", Keep: 7)));
    }

    [Theory]
    [InlineData(false, new string[0], false)]
    [InlineData(true, new string[0], true)]                               // an older guest names no reason
    [InlineData(true, new[] { "ssh-session" }, false)]                    // the client's watch alone
    [InlineData(true, new[] { "ssh-session", "agent-log:claude" }, true)]
    [InlineData(true, new[] { "t3-thread" }, true)]
    [InlineData(true, new[] { "provisioning" }, true)]
    public void Only_work_beyond_an_ssh_connection_makes_a_heartbeat_busy_for_backups(bool busy, string[] reasons, bool expected) =>
        Assert.Equal(expected, BackupSchedule.BusyForBackup(busy, reasons));
}
