namespace Constructd.Core.Domain;

public static class BackupTriggers
{
    public const string Scheduled = "scheduled";
    public const string Manual = "manual";
    public const string PreReinstall = "pre-reinstall";
    /// <summary>The trigger stored for a slot upload, whatever label the VM sent.</summary>
    public const string Slot = "slot";
    /// <summary>The labels a VM may send; the host decides what its upload counts as (docs/plans/construct-platform-design.md 10.2).</summary>
    public static bool IsValid(string? trigger) => trigger is Scheduled or Manual or PreReinstall;
}

/// <summary>
/// Who asked for a backup (docs/plans/construct-platform-design.md 10.2). A host-requested backup consumed an
/// open host request; a slot upload is one the host did not ask for. Rows stored before this mark existed
/// have none and count as host-requested.
/// </summary>
public static class BackupOrigins
{
    public const string Host = "host";
    public const string Slot = "slot";
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
    string? KeysJson = null,
    string? Origin = null,
    long? FileCount = null)
{
    public bool IsOpen => State == BackupStates.Open;
    public bool IsSlot => Origin == BackupOrigins.Slot;
    public bool IsComplete => State == BackupStates.Complete;
    public bool IsPinned(DateTimeOffset now) => PinnedUntil is { } until && until > now;
}

/// <summary>The last skipped or failed run, as the guest (or the upload route) reported it.</summary>
public sealed record BackupFailure(DateTimeOffset At, string Trigger, string Reason, string? Detail);

/// <summary>
/// Per <em>(owner, VM name)</em>: the owner's overrides (null = the host default) and the backup
/// status the heartbeat schedule and the status route read. <see cref="RequestPendingAt"/> and
/// <see cref="RequestTrigger"/> are the host's one open request (null trigger: a manual request stored
/// before the trigger was kept); <see cref="SlotLastAt"/> is when the VM last opened a slot session,
/// which is not schedule state.
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
    DateTimeOffset? VmMissingSince = null,
    string? RequestTrigger = null,
    DateTimeOffset? SlotLastAt = null);
