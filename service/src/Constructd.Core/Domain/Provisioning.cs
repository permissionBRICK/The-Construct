namespace Constructd.Core.Domain;

/// <summary>
/// The settings <c>bin/provision.sh</c> needs, as <c>Provision-AgentVM.ps1</c> takes them. Every value
/// is optional: null means the PowerShell parameter's default. The string toggles are passed to the
/// guest verbatim, as the PowerShell does (an empty value means "keep the VM's saved choice" where
/// <c>provision.sh</c> supports it). No field carries a secret: those travel in the job's secret bag.
/// </summary>
public sealed record ProvisionSettings
{
    /// <summary><c>AI_TOOLS</c>; default <c>opencode,claude-code,codex</c>. <see cref="VmDesiredState.CodingAgents"/>, when set, wins.</summary>
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
    /// <summary>
    /// <c>SWAP_SIZE_GB</c>: the guest's swap file, <c>auto</c> (half the RAM, at most 8 GiB) or a whole number of
    /// GiB from 0 (none) to 128. Null keeps the VM's saved choice, <c>auto</c> on a new VM.
    /// </summary>
    public string? SwapSizeGb { get; init; }
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
/// What a VM should look like after its next provision: the selected project profiles, the
/// settings and the coding agents (plan 11.8; null: <see cref="CodingAgents.Default"/>). The engine
/// keeps the last one per VM, so a reprovision needs no client.
/// </summary>
public sealed record VmDesiredState(IReadOnlyList<string> Projects, ProvisionSettings Settings, IReadOnlyList<string>? CodingAgents = null);

/// <summary>
/// The coding agents a VM can have, by their <c>AI_TOOLS</c> ids (plan 11.8). An agent left out is no longer
/// installed, updated or configured, and is off in T3 Code; it is not uninstalled.
/// </summary>
public static class CodingAgents
{
    public static readonly IReadOnlyList<string> Known = ["claude-code", "codex", "opencode", "antigravity"];

    /// <summary>What a VM without a choice gets, as before the choice existed.</summary>
    public static readonly IReadOnlyList<string> Default = ["opencode", "claude-code", "codex"];

    /// <summary>Known ids, each once.</summary>
    public static bool Valid(IReadOnlyList<string?> agents) =>
        agents.All(a => a is not null && Known.Contains(a)) && agents.Distinct(StringComparer.Ordinal).Count() == agents.Count;

    /// <summary><c>AI_TOOLS</c> for a desired state; <c>none</c> when every agent is off (an empty value means the default).</summary>
    public static string AiTools(VmDesiredState desired) => desired.CodingAgents switch
    {
        null => desired.Settings.AiTools ?? string.Join(',', Default),
        { Count: 0 } => "none",
        var chosen => string.Join(',', chosen),
    };

    /// <summary>The known ids in an <c>AI_TOOLS</c> value as a VM saved it (quotes undone); null when it names none and is empty.</summary>
    public static IReadOnlyList<string>? FromAiTools(string? value)
    {
        var text = (value ?? "").Trim().Trim('\'').Trim();
        if (text.Length == 0) return null;
        return text.Split(',').Select(t => t.Trim()).Where(Known.Contains).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>In <see cref="Known"/>'s order, so the same choice hashes the same.</summary>
    public static IReadOnlyList<string>? Canonical(IReadOnlyList<string>? agents) =>
        agents is null ? null : Known.Where(agents.Contains).ToArray();
}

/// <summary>
/// A stored desired state with its canonical hash (<c>CONSTRUCT_DESIRED_HASH</c>). <paramref name="VmCreated"/> is the
/// creation time of the VM it was stored for: the settings of an earlier VM under the same name are not this VM's
/// (null on rows written before it was recorded).
/// </summary>
public sealed record StoredDesiredState(VmDesiredState Desired, string Hash, DateTimeOffset UpdatedAt, string UpdatedBy,
    DateTimeOffset? VmCreated = null);

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
/// The durable result of a <see cref="JobKinds.Provision"/> job. It carries the failed guest steps and the fix prompt for
/// the repair agent (the PowerShell's autofix prompt). Failed optional guest steps alone leave it <paramref name="Succeeded"/>
/// (the job's warnings); <paramref name="FailedStep"/> and <paramref name="Code"/> name a step that failed the job. No secret.
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
/// The durable result of a <see cref="JobKinds.Restore"/> job: which backup, and where it stopped. A restore of a
/// pre-reinstall backup runs the project commands after it; <paramref name="FailedSteps"/> are the ones of them that
/// failed (the job's warnings). No secret.
/// </summary>
public sealed record RestoreJobResult(string BackupId, bool Succeeded, string? FailedStep, string? Code, string? Message,
    IReadOnlyList<ProvisionStepError>? FailedSteps = null);

/// <summary>
/// The durable result of a <see cref="JobKinds.Reinstall"/> job. <paramref name="BackupId"/> is the
/// pre-reinstall backup it relies on (kept on the host whatever happens); <paramref name="VmDeleted"/>
/// whether the old VM is gone; <paramref name="Restored"/> whether the backup is back in the new one;
/// <paramref name="Provision"/> the provisioning part, once it ran. <paramref name="RestoreNeeded"/>: the VM is
/// reinstalled and provisioned, but the backup could not be restored by the job (its key went with an engine restart);
/// a device restores it with a <c>restore</c> job of <paramref name="BackupId"/>, made at <paramref name="BackupCreatedAt"/>.
/// No secret.
/// </summary>
public sealed record ReinstallJobResult(string? BackupId, bool Succeeded, string? FailedStep, string? Code, string? Message,
    bool VmDeleted, bool Restored, ProvisionJobResult? Provision, bool RestoreNeeded = false, DateTimeOffset? BackupCreatedAt = null);
