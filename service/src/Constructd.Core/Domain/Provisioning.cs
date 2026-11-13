namespace Constructd.Core.Domain;

/// <summary>
/// The settings <c>bin/provision.sh</c> needs, as <c>Provision-AgentVM.ps1</c> takes them. Every value
/// is optional: null means the PowerShell parameter's default. The string toggles are passed to the
/// guest verbatim, as the PowerShell does (an empty value means "keep the VM's saved choice" where
/// <c>provision.sh</c> supports it). No field carries a secret: those travel in the job's secret bag.
/// </summary>
public sealed record ProvisionSettings
{
    /// <summary><c>AI_TOOLS</c>; default <c>opencode,claude-code,codex</c>.</summary>
    public string? AiTools { get; init; }
    /// <summary><c>SSH_USER</c>: the autoinstall seed user; default the engine's <c>Iso:SeedUser</c>.</summary>
    public string? SeedUser { get; init; }
    /// <summary><c>AGENT_NAME</c>; default <c>&lt;vm&gt;-agent</c> (the PowerShell's <c>$HostAlias-agent</c>).</summary>
    public string? AgentName { get; init; }
    /// <summary><c>CLAUDE_USER</c> (the PowerShell's <c>-RemoteUser</c>); default <c>root</c>.</summary>
    public string? ClaudeUser { get; init; }
    public string? GitUserName { get; init; }
    public string? GitEmail { get; init; }
    /// <summary><c>GIT_CREDENTIAL_STORE</c>; default false.</summary>
    public bool? GitCredentialStore { get; init; }
    /// <summary><c>GIT_CLONE_SKIP_HOSTS_B64</c>: base64 of newline-separated HTTP(S) origins.</summary>
    public string? GitCloneSkipHostsB64 { get; init; }
    /// <summary>
    /// <c>CHECKOUT_PROJECTS</c>. The PowerShell decides it from the selected profiles' <c>repos</c>, which
    /// the engine cannot read yet; the client states it. Default true: a skipped clone is the worse
    /// surprise, and <c>checkout-projects.sh</c> does nothing when no profile declares a repo.
    /// </summary>
    public bool? CheckoutProjects { get; init; }
    public string? VsCodeServer { get; init; }
    public string? VsCodeServeWeb { get; init; }
    public string? VsCodeTunnel { get; init; }
    /// <summary><c>VSCODE_CLIENT_COMMIT</c>: the desktop VS Code commit, when the client knows it.</summary>
    public string? VsCodeClientCommit { get; init; }
    public string? SmbShare { get; init; }
    public string? ClaudePartialStreaming { get; init; }
    public string? MicPassthrough { get; init; }
    public string? OpenCodeBackgroundWatcher { get; init; }
    public string? T3Code { get; init; }
    public string? T3CodeChannel { get; init; }
    public string? T3CodeBuildSource { get; init; }
    public string? T3CodePatched { get; init; }
    public string? T3CodeAutoApprove { get; init; }
    public string? T3CodeHttps { get; init; }
    public string? DockerRegistryMirrors { get; init; }
    /// <summary><c>CONSTRUCT_EXTERNAL_HOST</c>; sent (with the port) only when set.</summary>
    public string? ExternalHost { get; init; }
    /// <summary><c>CONSTRUCT_EXTERNAL_SSH_PORT</c>; default 22 when <see cref="ExternalHost"/> is set.</summary>
    public int? ExternalSshPort { get; init; }
    /// <summary><c>CONSTRUCT_SERVICE_URL</c>: set for a service-managed VM; empty is local mode.</summary>
    public string? ServiceUrl { get; init; }
    /// <summary><c>CONSTRUCT_SERVICE_CA_B64</c>: public certificate, sent only with a service URL.</summary>
    public string? ServiceCaB64 { get; init; }
    /// <summary><c>CONSTRUCT_INSTANCE_NAME</c>: sent only with a service URL; default the VM's name.</summary>
    public string? InstanceName { get; init; }
}

/// <summary>
/// What a VM should look like after its next provision: the selected project profiles and the
/// settings. The engine keeps the last one per VM, so a reprovision needs no client.
/// </summary>
public sealed record VmDesiredState(IReadOnlyList<string> Projects, ProvisionSettings Settings);

/// <summary>A stored desired state with its canonical hash (<c>CONSTRUCT_DESIRED_HASH</c>).</summary>
public sealed record StoredDesiredState(VmDesiredState Desired, string Hash, DateTimeOffset UpdatedAt, string UpdatedBy);

/// <summary>
/// What the guest says it was provisioned with, read from <c>/etc/construct/provisioned.env</c> after
/// a run. Null fields are keys the file does not have.
/// </summary>
public sealed record VmAppliedState(
    string? Commit,
    string? PayloadHash,
    string? DesiredHash,
    string? Channel,
    string? InstalledAt,
    string? ReprovisionedAt,
    DateTimeOffset ReadAt,
    string JobId);

/// <summary>One failed guest step, as <c>provision.sh</c>'s result block names it.</summary>
public sealed record ProvisionStepError(string Title, int ExitCode, string LogPath);

/// <summary>
/// The durable result of a <see cref="JobKinds.Provision"/> job. On failure it carries the failed
/// guest steps and the fix prompt for the repair agent (the PowerShell's autofix prompt). No secret.
/// </summary>
public sealed record ProvisionJobResult(
    string Commit,
    string PayloadHash,
    string DesiredHash,
    string? Channel,
    bool Succeeded,
    string? FailedStep,
    string? Code,
    IReadOnlyList<ProvisionStepError> FailedSteps,
    IReadOnlyList<string> FixPrompt,
    bool Rebooted,
    VmAppliedState? Applied);

/// <summary>
/// The durable result of a <see cref="JobKinds.Restore"/> job: which backup, and where it stopped. No secret.
/// </summary>
public sealed record RestoreJobResult(string BackupId, bool Succeeded, string? FailedStep, string? Code, string? Message);

/// <summary>
/// The durable result of a <see cref="JobKinds.Reinstall"/> job. <paramref name="BackupId"/> is the
/// pre-reinstall backup it relies on (kept on the host whatever happens); <paramref name="VmDeleted"/>
/// whether the old VM is gone; <paramref name="Restored"/> whether the backup is back in the new one;
/// <paramref name="Provision"/> the provisioning part, once it ran. No secret.
/// </summary>
public sealed record ReinstallJobResult(string? BackupId, bool Succeeded, string? FailedStep, string? Code, string? Message,
    bool VmDeleted, bool Restored, ProvisionJobResult? Provision);
