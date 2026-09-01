using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Tests.Core;

/// <summary>
/// The idle decision matrix of plan §4.7. The make-or-break case is "busy heartbeat, zero
/// connections": an agent running a long unattended job must keep its VM alive.
/// </summary>
public class IdleEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(5);
    private const int Grace = 3;

    private static IdleEvaluationInput Input(
        int timeoutMinutes = 60,
        IdleAction action = IdleAction.Save,
        VmState state = VmState.Running,
        int connections = 0,
        ActivityReport? report = null,
        int idleForMinutes = 90) =>
        new(
            "work-vm",
            state,
            new IdlePolicy(timeoutMinutes, action),
            Now,
            Now.AddMinutes(-idleForMinutes),
            connections,
            report,
            ReportInterval,
            Grace);

    private static ActivityReport Report(bool busy, int ageMinutes, params string[] reasons) =>
        new("work-vm", busy, reasons, Now.AddMinutes(-ageMinutes));

    [Fact]
    public void Busy_heartbeat_keeps_a_vm_alive_with_zero_connections()
    {
        var decision = IdleEvaluator.Evaluate(Input(report: Report(true, 1, "claude running")));

        Assert.Equal(IdleDecisionKind.KeepAlive, decision.Kind);
        Assert.Contains("claude running", decision.Reason);
    }

    [Fact]
    public void Connections_keep_a_vm_alive_without_any_heartbeat()
    {
        var decision = IdleEvaluator.Evaluate(Input(connections: 2, report: null));

        Assert.Equal(IdleDecisionKind.KeepAlive, decision.Kind);
        Assert.Contains("2 active connection", decision.Reason);
    }

    [Fact]
    public void Timeout_with_neither_signal_saves_the_vm()
    {
        var decision = IdleEvaluator.Evaluate(Input(report: Report(false, 1)));

        Assert.Equal(IdleDecisionKind.Save, decision.Kind);
        Assert.Contains("not busy", decision.Reason);
    }

    [Fact]
    public void Shutdown_policy_shuts_the_vm_down_instead()
    {
        var decision = IdleEvaluator.Evaluate(Input(action: IdleAction.Shutdown, report: Report(false, 1)));

        Assert.Equal(IdleDecisionKind.Shutdown, decision.Kind);
    }

    [Fact]
    public void Action_off_never_acts()
    {
        var decision = IdleEvaluator.Evaluate(Input(action: IdleAction.Off, idleForMinutes: 10_000));

        Assert.Equal(IdleDecisionKind.None, decision.Kind);
    }

    [Fact]
    public void Timeout_zero_never_acts()
    {
        var decision = IdleEvaluator.Evaluate(Input(timeoutMinutes: 0, idleForMinutes: 10_000));

        Assert.Equal(IdleDecisionKind.None, decision.Kind);
    }

    [Theory]
    [InlineData(VmState.Off)]
    [InlineData(VmState.Saved)]
    [InlineData(VmState.Paused)]
    [InlineData(VmState.Absent)]
    [InlineData(VmState.Unknown)]
    public void Only_running_vms_are_acted_on(VmState state)
    {
        var decision = IdleEvaluator.Evaluate(Input(state: state));

        Assert.Equal(IdleDecisionKind.None, decision.Kind);
    }

    [Fact]
    public void Inside_the_timeout_window_nothing_happens()
    {
        var decision = IdleEvaluator.Evaluate(Input(timeoutMinutes: 60, idleForMinutes: 30, report: Report(false, 1)));

        Assert.Equal(IdleDecisionKind.KeepAlive, decision.Kind);
        Assert.Contains("left of the 60m timeout", decision.Reason);
    }

    [Fact]
    public void A_busy_heartbeat_inside_the_grace_window_still_counts()
    {
        // 12 minutes old, grace = 3 x 5 minutes: still believed.
        var decision = IdleEvaluator.Evaluate(Input(report: Report(true, 12)));

        Assert.Equal(IdleDecisionKind.KeepAlive, decision.Kind);
    }

    [Fact]
    public void A_stale_busy_report_starts_the_timeout_only_when_the_grace_expires()
    {
        // Busy 20 minutes ago, grace = 15 minutes, timeout = 60: the guest counts as idle since
        // minute 15, so only 5 of the 60 timeout minutes have passed.
        var decision = IdleEvaluator.Evaluate(Input(report: Report(true, 20)));

        Assert.Equal(IdleDecisionKind.KeepAlive, decision.Kind);
        Assert.Contains("left of the 60m timeout", decision.Reason);
    }

    [Fact]
    public void A_crashed_guest_is_acted_on_once_grace_plus_timeout_have_passed()
    {
        // Busy 90 minutes ago: idle since minute 15, i.e. for 75 minutes — past the 60m timeout.
        var decision = IdleEvaluator.Evaluate(Input(report: Report(true, 90)));

        Assert.Equal(IdleDecisionKind.Save, decision.Kind);
        Assert.Contains("stale", decision.Reason);
    }

    [Fact]
    public void A_timeout_shorter_than_the_grace_window_still_waits_for_the_grace()
    {
        // Timeout 5m, grace 15m: nothing may happen before minute 15, and nothing has by minute 12.
        var inside = IdleEvaluator.Evaluate(Input(timeoutMinutes: 5, report: Report(true, 12)));
        Assert.Equal(IdleDecisionKind.KeepAlive, inside.Kind);

        // At minute 16 the grace has just expired — the 5m timeout starts running now, not earlier.
        var justAfterGrace = IdleEvaluator.Evaluate(Input(timeoutMinutes: 5, report: Report(true, 16)));
        Assert.Equal(IdleDecisionKind.KeepAlive, justAfterGrace.Kind);
        Assert.Contains("timeout", justAfterGrace.Reason);

        // At minute 21 it has elapsed.
        var elapsed = IdleEvaluator.Evaluate(Input(timeoutMinutes: 5, report: Report(true, 21)));
        Assert.Equal(IdleDecisionKind.Save, elapsed.Kind);
    }

    [Fact]
    public void A_never_reporting_vm_gets_the_grace_window_before_the_timeout_starts()
    {
        // Never reported, last active 60 minutes ago, timeout 60, grace 15: idle since minute 15,
        // so 45 minutes of the timeout have passed.
        var waiting = IdleEvaluator.Evaluate(Input(report: null, idleForMinutes: 60));
        Assert.Equal(IdleDecisionKind.KeepAlive, waiting.Kind);

        var elapsed = IdleEvaluator.Evaluate(Input(report: null, idleForMinutes: 76));
        Assert.Equal(IdleDecisionKind.Save, elapsed.Kind);
    }

    [Fact]
    public void An_explicit_not_busy_report_needs_no_extra_grace()
    {
        // The guest says it is idle, so the window runs from the last real activity, not from the
        // report: 90 minutes idle with a 60 minute timeout acts immediately.
        var decision = IdleEvaluator.Evaluate(Input(report: Report(false, 1), idleForMinutes: 90));

        Assert.Equal(IdleDecisionKind.Save, decision.Kind);
    }

    [Fact]
    public void A_vm_that_never_reported_and_has_no_connections_idles_out()
    {
        var decision = IdleEvaluator.Evaluate(Input(report: null));

        Assert.Equal(IdleDecisionKind.Save, decision.Kind);
        Assert.Contains("no heartbeat", decision.Reason);
    }

    [Fact]
    public void Report_freshness_is_measured_against_the_grace_multiple()
    {
        Assert.True(IdleEvaluator.IsReportFresh(Report(true, 14), Now, ReportInterval, Grace));
        Assert.False(IdleEvaluator.IsReportFresh(Report(true, 16), Now, ReportInterval, Grace));
        Assert.False(IdleEvaluator.IsReportFresh(null, Now, ReportInterval, Grace));
    }

    [Fact]
    public void Watermark_advances_while_connections_exist()
    {
        var previous = Now.AddHours(-2);

        var updated = IdleEvaluator.ComputeLastActiveAt(previous, Now, 1, null, ReportInterval, Grace);

        Assert.Equal(Now, updated);
    }

    [Fact]
    public void Watermark_advances_to_the_time_of_a_fresh_busy_report()
    {
        var previous = Now.AddHours(-2);
        var report = Report(true, 3);

        var updated = IdleEvaluator.ComputeLastActiveAt(previous, Now, 0, report, ReportInterval, Grace);

        Assert.Equal(report.ReportedAt, updated);
    }

    [Fact]
    public void Watermark_does_not_move_backwards_or_on_idle_reports()
    {
        var previous = Now.AddMinutes(-1);

        Assert.Equal(previous, IdleEvaluator.ComputeLastActiveAt(previous, Now, 0, Report(false, 0), ReportInterval, Grace));
        Assert.Equal(previous, IdleEvaluator.ComputeLastActiveAt(previous, Now, 0, Report(true, 30), ReportInterval, Grace));
        Assert.Equal(previous, IdleEvaluator.ComputeLastActiveAt(previous, Now, 0, Report(true, 3), ReportInterval, Grace));
    }
}
