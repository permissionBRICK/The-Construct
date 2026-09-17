using Constructd.Core.Domain;

namespace Constructd.Core.Configuration;

/// <summary>
/// Every service setting, bound from the <c>Constructd</c> configuration section (appsettings,
/// environment variables <c>Constructd__…</c>, command line). Documented in <c>service/README.md</c>.
/// </summary>
public sealed class ConstructdOptions
{
    /// <summary>Enable per-session, unprivileged Hyper-V console accounts for the trusted browser gateway.</summary>
    public bool BrowserConsoleEnabled { get; set; } = true;
    public HostAdminOptions HostAdmin { get; set; } = new();

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

    /// <summary>
    /// Which hypervisor platform this host runs on: <c>hyperv</c> (the default: the Windows host with
    /// the Hyper-V driver, WSL/native ISO media and netsh forwards) or <c>proxmox</c> (a Proxmox VE
    /// node: VMs are cloned from a cached Ubuntu cloud image and seeded with cloud-init through
    /// <c>qm</c>, and the service relays SSH/app forwards in-process). Fake mode ignores it.
    /// </summary>
    public string Backend { get; set; } = "hyperv";

    /// <summary>True when <see cref="Backend"/> selects the Proxmox VE platform.</summary>
    public bool IsProxmox => string.Equals(Backend?.Trim(), "proxmox", StringComparison.OrdinalIgnoreCase);

    public ProxmoxOptions Proxmox { get; set; } = new();

    public NegotiateOptions Negotiate { get; set; } = new();

    /// <summary>The Construct checkout the service invokes (ISO build, Create-AgentVM, …).</summary>
    public string ScriptsDir { get; set; } = string.Empty;

    /// <summary>WSL distro used for the ISO build. Empty uses WSL's default distro.</summary>
    public string WslDistro { get; set; } = "Ubuntu";

    /// <summary>LAN name/IP that forwards and endpoints are advertised on.</summary>
    public string PublicHost { get; set; } = "localhost";

    /// <summary>
    /// Optional per-VM host name template, e.g. <c>{name}.vpn.example</c> (plan §4.12): with a
    /// wildcard DNS record pointing at this host, every VM gets its OWN name, which is what lets two
    /// VMs serve web UIs that browsers keep apart (cookies are scoped by host, not by port).
    /// Rendered by <see cref="PublicHostFor"/> and validated at startup
    /// (<c>Constructd.Core.Logic.PublicHostPatternRules</c>). Empty — the default — means every VM is
    /// advertised on <see cref="PublicHost"/>, exactly as before this setting existed.
    /// The API certificate is unaffected: it stays bound to <see cref="PublicHost"/>.
    /// </summary>
    public string PublicHostPattern { get; set; } = string.Empty;

    /// <summary>
    /// The host name one VM's endpoint and forwards are advertised under: the rendered
    /// <see cref="PublicHostPattern"/>, or <see cref="PublicHost"/> when no pattern is configured.
    /// </summary>
    public string PublicHostFor(string? vmName) =>
        Logic.PublicHostPatternRules.Resolve(PublicHostPattern, PublicHost, vmName);

    /// <summary>
    /// Hyper-V virtual switch new VMs are attached to. The service host's own switch (plan §4.4
    /// creates an internal NAT switch at install); the default is Hyper-V's <c>Default Switch</c>, so
    /// a host that has nothing else configured still works.
    /// </summary>
    public string SwitchName { get; set; } = "Default Switch";

    /// <summary>
    /// Directory the per-VM VHDX is created in. Empty (the default) leaves the path to the hypervisor
    /// driver, which uses Hyper-V's own default folder
    /// (<c>C:\ProgramData\Microsoft\Windows\Virtual Hard Disks</c>).
    /// </summary>
    public string VmStorageRoot { get; set; } = string.Empty;

    /// <summary>
    /// Address the host's portproxy rules listen on. <c>0.0.0.0</c> is every interface; narrow it to
    /// one LAN address on a multi-homed host.
    /// </summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>Windows PowerShell used for the Hyper-V driver. PowerShell 5.1 — <em>not</em> pwsh.</summary>
    public string PowerShellPath { get; set; } = "powershell.exe";

    /// <summary>WSL launcher used for the ISO build.</summary>
    public string WslPath { get; set; } = "wsl.exe";

    /// <summary>netsh used for the host's port-proxy rules.</summary>
    public string NetshPath { get; set; } = "netsh.exe";

    /// <summary>Port range for per-VM SSH forwards.</summary>
    public PortRangeOptions SshForwardPorts { get; set; } = new(2201, 2299);

    /// <summary>Port range for app forwards created through <c>construct expose --to host</c>.</summary>
    public PortRangeOptions AppForwardPorts { get; set; } = new(2300, 2999);

    /// <summary>Cap on extra forwards per VM (the scoped VM token is "capped count/range", plan §4.6).</summary>
    public int MaxForwardsPerVm { get; set; } = 16;

    /// <summary>Retry host forward reconciliation after boot and DHCP changes. Zero disables periodic passes.</summary>
    public int ForwardReconcileSeconds { get; set; } = 30;

    /// <summary>How long VM creation waits for SSH to answer.</summary>
    public int VmReachableTimeoutMinutes { get; set; } = 30;

    /// <summary>Default page size of <c>GET /audit</c>.</summary>
    public int AuditQueryLimit { get; set; } = 200;

    public IdleOptions Idle { get; set; } = new();

    public IsoOptions Iso { get; set; } = new();

    public PowerOptions Power { get; set; } = new();

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

/// <summary>
/// The Proxmox VE platform (<c>Constructd:Backend = proxmox</c>). The service runs ON the node, as a
/// user that may run <c>qm</c> and <c>pvesh</c> (root), so no API token and no TLS pin towards the
/// node exist: the client pins the SERVICE's certificate exactly as it does for a Windows host.
/// <c>service/host/install-construct-host.sh</c> writes these values.
/// </summary>
public sealed class ProxmoxOptions
{
    /// <summary>Proxmox node name. Empty (the default) means the node this service runs on.</summary>
    public string Node { get; set; } = string.Empty;

    /// <summary>Storage that receives each VM's disk and cloud-init drive (<c>images</c> content).</summary>
    public string Storage { get; set; } = "local-lvm";

    /// <summary>
    /// The cached Ubuntu cloud image every VM is cloned from, as a Proxmox volume id on a storage with
    /// <c>import</c> content (<c>qm create --scsi0 &lt;storage&gt;:0,import-from=&lt;this&gt;</c>).
    /// </summary>
    public string ImageVolume { get; set; } = "local:import/construct-ubuntu-noble-cloudimg-amd64.qcow2";

    /// <summary>Storage whose <c>snippets</c> directory holds the per-VM cloud-init user data.</summary>
    public string SnippetStorage { get; set; } = "local";

    /// <summary>The directory behind <see cref="SnippetStorage"/>'s snippets content, on this node.</summary>
    public string SnippetDir { get; set; } = "/var/lib/vz/snippets";

    /// <summary>Bridge new VMs are attached to; the guest takes a DHCP lease on it.</summary>
    public string Bridge { get; set; } = "vmbr0";

    /// <summary>QEMU CPU type. <c>host</c> passes the node's CPU through, which is what nested KVM needs.</summary>
    public string CpuType { get; set; } = "host";

    /// <summary>CPU model used when virtualization extensions are disabled.</summary>
    public string CpuTypeWithoutNesting { get; set; } = "x86-64-v2-AES";

    /// <summary>The <c>qm</c> command (VM lifecycle).</summary>
    public string QmPath { get; set; } = "qm";

    /// <summary>The <c>pvesh</c> command (read-only API queries).</summary>
    public string PveshPath { get; set; } = "pvesh";
}

/// <summary>
/// Kerberos/NTLM sign-in (the Negotiate scheme). On Windows it is on by default and needs nothing:
/// the service runs as a domain identity. On Linux it is off unless <see cref="Enabled"/> says
/// otherwise; then the service authenticates through GSSAPI with a keytab for its
/// <c>HTTP/&lt;public host&gt;</c> principal (<c>KRB5_KTNAME</c>, written by the installer), and the
/// identity Kerberos yields — <c>user@REALM</c> — is mapped to the <c>DOMAIN\user</c> form a Windows
/// host produces, so one user record serves both kinds of host.
/// </summary>
public sealed class NegotiateOptions
{
    /// <summary>Register the Negotiate scheme. Null (the default) means "on Windows only".</summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// The NetBIOS domain name (<c>HOME</c>) a Kerberos principal <c>user@REALM</c> is mapped onto
    /// (<c>HOME\user</c>). Empty leaves principal names as Kerberos reports them.
    /// </summary>
    public string DomainName { get; set; } = string.Empty;

    /// <summary>
    /// The Kerberos realm (<c>DC.EXAMPLE.NET</c>) whose principals are mapped. Empty maps every realm;
    /// set it when the service can be reached by principals from a trusted foreign realm.
    /// </summary>
    public string Realm { get; set; } = string.Empty;
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

/// <summary>Host power behaviour while VMs are running (plan §4.13).</summary>
public sealed class PowerOptions
{
    /// <summary>
    /// Hold a Windows power availability request while at least one service-managed VM is running,
    /// so the host does not go to sleep under them. On by default — a host serving VMs to other
    /// people has no business sleeping. Turn it off where the host's own power plan is managed
    /// elsewhere; the service then never takes a request.
    /// </summary>
    public bool KeepHostAwake { get; set; } = true;
}

/// <summary>
/// How install media is built. One strategy per value; composition maps the value to an
/// <see cref="Abstractions.IIsoBuilder"/> in exactly one place, so a new strategy is a
/// new implementation and one more case there.
/// </summary>
public enum IsoBuildMode
{
    /// <summary>
    /// Compatibility name for native catalog media. Reuse published media, build missing media
    /// on demand, and download and rebuild when a client requests redownload.
    /// </summary>
    Prebuilt,

    /// <summary>
    /// The service builds one ISO per VM itself, through <c>wsl.exe</c>. Usable wherever the service
    /// identity can run WSL; it bakes the VM's hostname into the seed.
    /// </summary>
    PerVm,

    /// <summary>Build generic media on demand with the self-contained .NET tool; cache it through the catalog.</summary>
    Native,

    /// <summary>
    /// Planned: build inside an existing Construct VM over SSH — where xorriso already is —
    /// and copy the result back. This is how the system will self-update its install media.
    /// </summary>
    InGuest,

    /// <summary>
    /// Planned: build natively on the hypervisor host (xorriso on a Proxmox node), which is
    /// the regular autoinstall path once Hyper-V is not the only backend.
    /// </summary>
    HypervisorHost,
}

/// <summary>Inputs for the autoinstall ISO build.</summary>
public sealed class IsoOptions
{
    /// <summary>Absolute path to the self-contained Construct.Iso executable resolved by the host installer.</summary>
    public string NativeBuilderPath { get; set; } = string.Empty;
    /// <summary>
    /// Which build strategy is in effect. <see cref="IsoBuildMode.Prebuilt"/> by default — see the
    /// enum for why, and <c>service/README.md</c> ("ISO build strategies") for the whole picture.
    /// </summary>
    public IsoBuildMode Mode { get; set; } = IsoBuildMode.Prebuilt;

    /// <summary>
    /// Where a guest built from GENERIC media takes its hostname at first boot — the
    /// <c>VM_HOSTNAME_SOURCE</c> of <c>bin/build-autoinstall-iso.sh</c>. <c>hyperv-kvp</c> reads the
    /// Hyper-V VM name out of the KVP data-exchange pool; <c>cloud-init-metadata</c> is the planned
    /// value for Proxmox / NoCloud / ConfigDrive, where the hypervisor supplies identity natively.
    ///
    /// It matters beyond cosmetics: the driver contract resolves a VM as <c>&lt;name&gt;.mshome.net</c>,
    /// and that name is the guest's OWN hostname as the switch's DNS learned it.
    /// </summary>
    public string HostnameSource { get; set; } = "hyperv-kvp";

    /// <summary>Seed user created by the unattended install (before provisioning takes over).</summary>
    public string SeedUser { get; set; } = "construct";

    /// <summary>Public key injected as the bootstrap key the client provisioner authenticates with.</summary>
    public string BootstrapPublicKeyPath { get; set; } = string.Empty;

    /// <summary>
    /// Ubuntu server ISO the autoinstall image is remastered from. Set this <em>or</em>
    /// <see cref="SourceUrl"/>; a path wins and is never downloaded or deleted.
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Where to download the source ISO from when <see cref="SourcePath"/> is empty. Admin-configured
    /// on purpose: the service does not go looking for "the current LTS" behind the admin's back, so a
    /// host's guests do not change release because a mirror did.
    /// </summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>Expected SHA-256 of the downloaded source ISO. Empty skips the check.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Directory holding the downloaded source ISO and the per-VM autoinstall ISOs.</summary>
    public string CacheDir { get; set; } = @"C:\ProgramData\Construct\service\iso";

    /// <summary>
    /// Ubuntu install source id baked into the autoinstall seed (<c>SOURCE_ID</c> of
    /// <c>bin/build-autoinstall-iso.sh</c>).
    /// </summary>
    public string SourceId { get; set; } = "ubuntu-server-minimal";
}
