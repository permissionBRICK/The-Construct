namespace Constructd.Core.Domain;

/// <summary>Role of a service user. Admins may manage users, tokens and every VM.</summary>
public enum Role
{
    User,
    Admin,
}

/// <summary>
/// Lifecycle state of a VM as reported by the hypervisor driver (plan §4.2; <c>Saved</c> is the
/// Hyper-V <c>Save-VM</c> state the idle engine puts VMs into).
/// </summary>
public enum VmState
{
    Unknown,
    Running,
    Off,
    Paused,
    Saved,
    Absent,
}

/// <summary>What the idle engine does when a VM has been idle for the whole timeout window.</summary>
public enum IdleAction
{
    /// <summary>Hyper-V <c>Save-VM</c>: state to disk, RAM freed, transparent resume.</summary>
    Save,
    Shutdown,
    /// <summary>Never idle out.</summary>
    Off,
}

/// <summary>
/// Where a port forward is materialized (plan §4.6). <c>Client</c> is the default: the forward is
/// only recorded by the service and relayed to the owner's extension, which opens it on the user's
/// PC. <c>Host</c> forwards are materialized on the service host (netsh portproxy) and are
/// LAN-reachable, which is why an admin can disable them per user.
/// </summary>
public enum ForwardTarget
{
    Client,
    Host,
}

/// <summary>State of a long-running job (POST → 202 + jobId, progress over SSE).</summary>
public enum JobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Outcome recorded on an audit entry.</summary>
public enum AuditOutcome
{
    Success,
    Denied,
    Failure,
}
