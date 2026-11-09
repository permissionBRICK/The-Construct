namespace Constructd.Core.Domain;

public static class BackupTriggers
{
    public const string Scheduled = "scheduled";
    public const string Manual = "manual";
    public const string PreReinstall = "pre-reinstall";
    public static bool IsValid(string? trigger) => trigger is Scheduled or Manual or PreReinstall;
}

public static class BackupStates
{
    public const string Open = "open";
    public const string Complete = "complete";
}

/// <summary>
/// One backup of a VM's config export, owned by <em>(owner, VM name)</em> rather than by one VM
/// incarnation, so it survives a reinstall. The payload is an age file on the host's disk; the host
/// stores the recipients' wrapped keys (<see cref="KeysJson"/>) but can open neither.
/// </summary>
public sealed record ConfigBackup(
    string Id,
    string Owner,
    string Vm,
    string Trigger,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt = null,
    long? SizeBytes = null,
    string? Sha256 = null,
    DateTimeOffset? PinnedUntil = null,
    string? KeysJson = null)
{
    public bool IsOpen => State == BackupStates.Open;
    public bool IsComplete => State == BackupStates.Complete;
    public bool IsPinned(DateTimeOffset now) => PinnedUntil is { } until && until > now;
}

/// <summary>The last skipped or failed run, as the guest (or the upload route) reported it.</summary>
public sealed record BackupFailure(DateTimeOffset At, string Trigger, string Reason, string? Detail);

/// <summary>
/// Per <em>(owner, VM name)</em>: the owner's overrides (null = the host default) and the backup
/// status the heartbeat schedule and the status route read.
/// </summary>
public sealed record BackupVmState(
    string Owner,
    string Vm,
    int? IntervalHours = null,
    int? Keep = null,
    DateTimeOffset? LastAttemptAt = null,
    DateTimeOffset? LastSuccessAt = null,
    BackupFailure? LastFailure = null,
    DateTimeOffset? FailingSince = null,
    DateTimeOffset? RequestPendingAt = null,
    DateTimeOffset? VmMissingSince = null);
