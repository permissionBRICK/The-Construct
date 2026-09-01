using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

/// <summary>What the idle evaluator concluded for one VM.</summary>
public enum IdleDecisionKind
{
    /// <summary>Nothing to do — the VM is not in a state the policy applies to.</summary>
    None,

    /// <summary>The VM is alive or still inside its timeout window.</summary>
    KeepAlive,

    /// <summary>Suspend to disk (<see cref="IdleAction.Save"/>).</summary>
    Save,

    /// <summary>Graceful shutdown (<see cref="IdleAction.Shutdown"/>).</summary>
    Shutdown,
}

/// <param name="Reason">Short, loggable explanation — ends up in the audit detail.</param>
public sealed record IdleDecision(IdleDecisionKind Kind, string Reason)
{
    public bool Acts => Kind is IdleDecisionKind.Save or IdleDecisionKind.Shutdown;
}

/// <summary>
/// Everything the evaluator needs about one VM at one instant.
/// </summary>
/// <param name="LastActiveAt">
/// When the VM was last observed active (connections &gt; 0, or a fresh busy heartbeat). Maintained
/// across ticks by <see cref="IdleEvaluator.ComputeLastActiveAt"/>.
/// </param>
/// <param name="ReportInterval">The heartbeat interval the guest reporter is configured with.</param>
/// <param name="MissingReportGraceMultiple">
/// How many report intervals a heartbeat may be missing before the guest counts as idle (plan §4.7:
/// "missing heartbeats count as idle only after a grace multiple").
/// </param>
public sealed record IdleEvaluationInput(
    string VmName,
    VmState State,
    IdlePolicy Policy,
    DateTimeOffset Now,
    DateTimeOffset LastActiveAt,
    int ActiveConnections,
    ActivityReport? LastReport,
    TimeSpan ReportInterval,
    int MissingReportGraceMultiple);

/// <summary>
/// The pure idle decision logic of plan §4.7. A VM is idle only when BOTH halves hold continuously
/// for the timeout window: no client connections through any forward, and no in-guest activity.
/// A long unattended agent job therefore keeps a VM alive with zero connections — that is the whole
/// point of unattended agents, so <c>busy</c> always wins.
/// </summary>
public static class IdleEvaluator
{
    /// <summary>
    /// True while the last heartbeat still counts. A <c>busy</c> report keeps the VM alive until it
    /// goes stale; a crashed guest (no reports at all) only counts as idle after the grace window.
    /// </summary>
    public static bool IsReportFresh(
        ActivityReport? report,
        DateTimeOffset now,
        TimeSpan reportInterval,
        int graceMultiple)
    {
        if (report is null)
        {
            return false;
        }

        var grace = reportInterval * Math.Max(1, graceMultiple);
        return now - report.ReportedAt <= grace;
    }

    /// <summary>
    /// True when the VM currently looks active: someone is connected, or the guest reported busy
    /// recently enough to still be believed.
    /// </summary>
    public static bool IsActiveNow(
        int activeConnections,
        ActivityReport? report,
        DateTimeOffset now,
        TimeSpan reportInterval,
        int graceMultiple) =>
        activeConnections > 0 ||
        (report is { Busy: true } && IsReportFresh(report, now, reportInterval, graceMultiple));

    /// <summary>
    /// When the guest starts counting as idle, given the last heartbeat:
    /// <list type="bullet">
    /// <item>an explicit <c>busy: false</c> report — right away: the guest itself says it is idle, so
    /// the window runs from whenever the VM was last actually active;</item>
    /// <item>a <c>busy</c> report that has gone stale — only once the grace window after it has
    /// expired, never retroactively from the report itself;</item>
    /// <item>no report at all (no reporter installed, or a guest that crashed before its first
    /// heartbeat) — only a grace window after the VM was last seen active.</item>
    /// </list>
    /// This is what plan §4.7's "missing heartbeats count as idle only after a grace multiple" means:
    /// silence buys the guest a grace window, and only then does the timeout start running.
    /// </summary>
    public static DateTimeOffset GuestIdleSince(
        ActivityReport? report,
        DateTimeOffset lastActiveAt,
        TimeSpan reportInterval,
        int graceMultiple)
    {
        var grace = reportInterval * Math.Max(1, graceMultiple);

        return report switch
        {
            null => lastActiveAt + grace,
            { Busy: true } => report.ReportedAt + grace,
            _ => lastActiveAt,
        };
    }

    /// <summary>
    /// The instant from which the VM has been continuously idle: the later of "last seen active" and
    /// the point at which the guest started counting as idle.
    /// </summary>
    public static DateTimeOffset IdleSince(
        ActivityReport? report,
        DateTimeOffset lastActiveAt,
        TimeSpan reportInterval,
        int graceMultiple)
    {
        var guestIdleSince = GuestIdleSince(report, lastActiveAt, reportInterval, graceMultiple);
        return guestIdleSince > lastActiveAt ? guestIdleSince : lastActiveAt;
    }

    /// <summary>
    /// Advances the "last seen active" watermark. Called once per tick per VM before
    /// <see cref="Evaluate"/>, and whenever a heartbeat arrives, so that the timeout measures a
    /// <em>continuous</em> idle window rather than a snapshot.
    /// </summary>
    public static DateTimeOffset ComputeLastActiveAt(
        DateTimeOffset previousLastActiveAt,
        DateTimeOffset now,
        int activeConnections,
        ActivityReport? report,
        TimeSpan reportInterval,
        int graceMultiple)
    {
        if (!IsActiveNow(activeConnections, report, now, reportInterval, graceMultiple))
        {
            return previousLastActiveAt;
        }

        // A busy report timestamps the activity itself; connections are only observable now.
        var activeAt = activeConnections > 0 || report is null
            ? now
            : report.ReportedAt;

        return activeAt > previousLastActiveAt ? activeAt : previousLastActiveAt;
    }

    /// <summary>Decides what to do with one VM. Pure: no clock, no I/O.</summary>
    public static IdleDecision Evaluate(IdleEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Policy.Action == IdleAction.Off)
        {
            return new IdleDecision(IdleDecisionKind.None, "policy action is off");
        }

        if (input.Policy.TimeoutMinutes <= 0)
        {
            return new IdleDecision(IdleDecisionKind.None, "policy timeout is 0 (off)");
        }

        if (input.State != VmState.Running)
        {
            return new IdleDecision(IdleDecisionKind.None, $"vm is {input.State.ToString().ToLowerInvariant()}");
        }

        if (input.ActiveConnections > 0)
        {
            return new IdleDecision(
                IdleDecisionKind.KeepAlive,
                $"{input.ActiveConnections} active connection(s)");
        }

        var reportFresh = IsReportFresh(
            input.LastReport,
            input.Now,
            input.ReportInterval,
            input.MissingReportGraceMultiple);

        if (input.LastReport is { Busy: true } && reportFresh)
        {
            var reasons = input.LastReport.Reasons.Count > 0
                ? string.Join(", ", input.LastReport.Reasons)
                : "no reasons given";
            return new IdleDecision(IdleDecisionKind.KeepAlive, $"guest busy: {reasons}");
        }

        var idleSince = IdleSince(
            input.LastReport,
            input.LastActiveAt,
            input.ReportInterval,
            input.MissingReportGraceMultiple);

        var timeout = TimeSpan.FromMinutes(input.Policy.TimeoutMinutes);

        if (input.Now < idleSince)
        {
            // Inside the grace window after a stale/absent heartbeat: not idle yet at all.
            return new IdleDecision(
                IdleDecisionKind.KeepAlive,
                $"within the heartbeat grace window ({Describe(idleSince - input.Now)} left)");
        }

        var idleFor = input.Now - idleSince;
        if (idleFor < timeout)
        {
            var remaining = timeout - idleFor;
            return new IdleDecision(
                IdleDecisionKind.KeepAlive,
                $"idle for {Describe(idleFor)}, {Describe(remaining)} left of the {input.Policy.TimeoutMinutes}m timeout");
        }

        var staleNote = input.LastReport is null
            ? "no heartbeat ever received"
            : reportFresh ? "guest reported not busy" : "heartbeat stale";

        return input.Policy.Action switch
        {
            IdleAction.Shutdown => new IdleDecision(
                IdleDecisionKind.Shutdown,
                $"idle for {Describe(idleFor)} (no connections, {staleNote})"),
            _ => new IdleDecision(
                IdleDecisionKind.Save,
                $"idle for {Describe(idleFor)} (no connections, {staleNote})"),
        };
    }

    private static string Describe(TimeSpan span) =>
        $"{(int)Math.Round(span.TotalMinutes, MidpointRounding.AwayFromZero)}m";
}
