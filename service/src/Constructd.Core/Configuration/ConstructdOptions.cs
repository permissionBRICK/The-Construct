using Constructd.Core.Domain;

namespace Constructd.Core.Configuration;

/// <summary>
/// Every service setting, bound from the <c>Constructd</c> configuration section (appsettings,
/// environment variables <c>Constructd__…</c>, command line). Documented in <c>service/README.md</c>.
/// </summary>
public sealed class ConstructdOptions
{
    public const string SectionName = "Constructd";

    /// <summary>
    /// Run with the in-memory fakes instead of the platform implementations (<c>--fake</c>).
    /// Development only: it also enables the test-identity auth scheme.
    /// </summary>
    public bool Fake { get; set; }

    /// <summary>
    /// Where users, tokens, VMs, jobs and the audit trail live: <c>Sqlite</c> (the default) or
    /// <c>Memory</c>. Fake mode defaults to <c>Memory</c>; combining <c>Fake</c> with <c>Sqlite</c>
    /// is a useful development and test mode — real persistence, no Hyper-V.
    /// </summary>
    public PersistenceMode? Persistence { get; set; }

    /// <summary>SQLite database file. Defaults to <c>constructd.db</c> next to the service.</summary>
    public string DatabasePath { get; set; } = "constructd.db";

    /// <summary>The persistence actually in effect.</summary>
    public PersistenceMode EffectivePersistence =>
        Persistence ?? (Fake ? PersistenceMode.Memory : PersistenceMode.Sqlite);

    /// <summary>URL the service listens on, e.g. <c>https://0.0.0.0:7462</c>.</summary>
    public string? ListenUrl { get; set; }

    /// <summary>PFX path for the TLS certificate (alternative to <see cref="CertThumbprint"/>).</summary>
    public string? CertPath { get; set; }

    /// <summary>Password for <see cref="CertPath"/>. Supply via environment or a user secret.</summary>
    public string? CertPassword { get; set; }

    /// <summary>Thumbprint of a certificate in the Windows store (what the client pins at enrollment).</summary>
    public string? CertThumbprint { get; set; }

    /// <summary>The Construct checkout the service invokes (ISO build, Create-AgentVM, …).</summary>
    public string ScriptsDir { get; set; } = string.Empty;

    /// <summary>WSL distro used for the ISO build.</summary>
    public string WslDistro { get; set; } = "Ubuntu";

    /// <summary>LAN name/IP that forwards and endpoints are advertised on.</summary>
    public string PublicHost { get; set; } = "localhost";

    /// <summary>Port range for per-VM SSH forwards.</summary>
    public PortRangeOptions SshForwardPorts { get; set; } = new(2201, 2299);

    /// <summary>Port range for app forwards created through <c>construct expose --to host</c>.</summary>
    public PortRangeOptions AppForwardPorts { get; set; } = new(2300, 2999);

    /// <summary>Cap on extra forwards per VM (the scoped VM token is "capped count/range", plan §4.6).</summary>
    public int MaxForwardsPerVm { get; set; } = 16;

    /// <summary>How long VM creation waits for SSH to answer.</summary>
    public int VmReachableTimeoutMinutes { get; set; } = 30;

    /// <summary>Default page size of <c>GET /audit</c>.</summary>
    public int AuditQueryLimit { get; set; } = 200;

    public IdleOptions Idle { get; set; } = new();

    public IsoOptions Iso { get; set; } = new();

    /// <summary>
    /// Identity seeded as the first admin when the user store is empty (on Windows: the domain
    /// account that installed the service, which then authenticates via Negotiate).
    /// </summary>
    public string? BootstrapAdmin { get; set; }

    /// <summary>Quota given to the bootstrap admin.</summary>
    public int BootstrapAdminMaxVms { get; set; } = 10;

    /// <summary>
    /// Optional plaintext token for the bootstrap admin, hashed at startup. Only for bootstrapping a
    /// host that cannot use Negotiate; it is never logged and should be removed from config once a
    /// real token has been issued.
    /// </summary>
    public string? BootstrapAdminToken { get; set; }
}

/// <summary>Where durable state is kept.</summary>
public enum PersistenceMode
{
    /// <summary>In-process only; everything is lost when the service stops.</summary>
    Memory,

    /// <summary>A SQLite file (hand-written SQL, no ORM).</summary>
    Sqlite,
}

/// <summary>An inclusive port range.</summary>
public sealed class PortRangeOptions
{
    public PortRangeOptions()
    {
    }

    public PortRangeOptions(int start, int end)
    {
        Start = start;
        End = end;
    }

    public int Start { get; set; }

    public int End { get; set; }
}

/// <summary>Idle policy defaults, cap and scheduler settings (plan §4.7).</summary>
public sealed class IdleOptions
{
    /// <summary>Run the background evaluator. Tests turn this off and call the engine directly.</summary>
    public bool SchedulerEnabled { get; set; } = true;

    /// <summary>Tick interval of the evaluator.</summary>
    public int TickSeconds { get; set; } = 60;

    /// <summary>Policy a new VM gets when the client does not specify one.</summary>
    public int DefaultTimeoutMinutes { get; set; } = 120;

    public IdleAction DefaultAction { get; set; } = IdleAction.Save;

    /// <summary>Admin cap on user-chosen timeouts; 0 means no cap.</summary>
    public int MaxTimeoutMinutes { get; set; }

    /// <summary>With a cap set, also forbid switching idling off entirely.</summary>
    public bool ForceEnabled { get; set; }

    /// <summary>The interval the in-guest reporter posts heartbeats at.</summary>
    public int ReportIntervalMinutes { get; set; } = 5;

    /// <summary>How many intervals a heartbeat may be missing before the guest counts as idle.</summary>
    public int MissingReportGraceMultiple { get; set; } = 3;
}

/// <summary>Inputs for the autoinstall ISO build.</summary>
public sealed class IsoOptions
{
    /// <summary>Seed user created by the unattended install (before provisioning takes over).</summary>
    public string SeedUser { get; set; } = "construct";

    /// <summary>Public key injected as the bootstrap key the client provisioner authenticates with.</summary>
    public string BootstrapPublicKeyPath { get; set; } = string.Empty;
}
