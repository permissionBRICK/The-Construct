using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

/// <summary>What the heartbeat reply tells the guest: start a backup now, and why.</summary>
public sealed record BackupDecision(bool Due, string? Trigger)
{
    public static BackupDecision NotDue { get; } = new(false, null);
}

/// <summary>
/// The host decides when a VM backs itself up (docs/plans/host-config-backups.md §3.4 and §3.6); the
/// guest never schedules on its own. Pure, so the heartbeat handler, the status route and the tests
/// share one rule.
/// </summary>
public static class BackupSchedule
{
    /// <summary>A manual request nobody answered with a session expires after this.</summary>
    public static TimeSpan RequestLifetime { get; } = TimeSpan.FromHours(1);

    /// <summary>Retries after an attempt wait <c>min(interval, RetryCap)</c>.</summary>
    public static TimeSpan RetryCap { get; } = TimeSpan.FromHours(6);

    /// <summary>A due scheduled backup waits for an idle heartbeat at most this long.</summary>
    public static TimeSpan BusyGrace { get; } = TimeSpan.FromHours(6);

    /// <summary>An open session older than this is abandoned: it neither blocks a new one nor survives cleanup.</summary>
    public static TimeSpan StaleSession { get; } = TimeSpan.FromHours(2);

    /// <summary>The interval in effect: the VM's override or the host default; a host interval of 0 turns scheduling off everywhere.</summary>
    public static int EffectiveIntervalHours(BackupsConfig host, BackupVmState? vm) =>
        host.IntervalHours == 0 ? 0 : vm?.IntervalHours ?? host.IntervalHours;

    /// <summary>The number kept: the VM's override or the host default, never above the host's <c>MaxKeep</c>.</summary>
    public static int EffectiveKeep(BackupsConfig host, BackupVmState? vm) =>
        Math.Clamp(vm?.Keep ?? host.Keep, 1, Math.Max(1, host.MaxKeep));

    public static bool RequestPending(BackupVmState? vm, DateTimeOffset now) =>
        vm?.RequestPendingAt is { } at && now - at < RequestLifetime;

    /// <summary>
    /// When the next scheduled backup becomes due (in the past when it already is), or the pending
    /// request's time; null when backups are off or scheduling is off for this VM. "No success yet"
    /// counts as due since the VM was created.
    /// </summary>
    public static DateTimeOffset? NextDueAt(BackupsConfig host, BackupVmState? vm, DateTimeOffset vmCreated, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!host.Enabled) return null;
        if (RequestPending(vm, now)) return vm!.RequestPendingAt;
        return ScheduledDueAt(host, vm, vmCreated);
    }

    public static BackupDecision Evaluate(BackupsConfig host, BackupVmState? vm, bool openSession, bool busy,
        DateTimeOffset vmCreated, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!host.Enabled || openSession) return BackupDecision.NotDue;
        if (RequestPending(vm, now)) return new(true, BackupTriggers.Manual);
        if (ScheduledDueAt(host, vm, vmCreated) is not { } dueAt || now < dueAt) return BackupDecision.NotDue;
        // A busy VM gets its scheduled backup on its next idle heartbeat, unless it has been due too long.
        if (busy && now - dueAt < BusyGrace) return BackupDecision.NotDue;
        return new(true, BackupTriggers.Scheduled);
    }

    private static DateTimeOffset? ScheduledDueAt(BackupsConfig host, BackupVmState? vm, DateTimeOffset vmCreated)
    {
        var hours = EffectiveIntervalHours(host, vm);
        if (hours <= 0) return null;
        var interval = TimeSpan.FromHours(hours);
        var bySuccess = vm?.LastSuccessAt is { } success ? success + interval : vmCreated;
        var retry = interval < RetryCap ? interval : RetryCap;
        return vm?.LastAttemptAt is { } attempt && attempt + retry > bySuccess ? attempt + retry : bySuccess;
    }
}
