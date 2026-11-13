using Constructd.Api.Infrastructure;
using Constructd.Core.Domain;

namespace Constructd.Api.Contracts;

/// <summary>
/// The secrets a provision run needs, kept in the job's RAM bag only (design 7.1, 7.4): never stored,
/// never in a log, a progress line or an argument. A retried job is sent them again.
/// </summary>
/// <param name="VmTokenB64"><c>CONSTRUCT_VM_TOKEN_B64</c>: only for a run that installs a new VM token.</param>
/// <param name="GitCloneCredentialsB64"><c>GIT_CLONE_CREDENTIALS_B64</c>: verified clone credentials.</param>
/// <param name="ServeWebTokenB64"><c>VSCODE_SERVE_WEB_TOKEN_B64</c>: a saved serve-web token to keep.</param>
/// <param name="SudoPassword">The connecting user's sudo password, when that user is not root and sudo asks for one.</param>
/// <param name="BackupKey">A <c>restore</c> job's backup key <c>E</c> (<c>AGE-SECRET-KEY-1…</c>), unwrapped by a device.</param>
public sealed record ProvisionSecretsRequest(
    [property: ConstructSecret] string? VmTokenB64 = null,
    [property: ConstructSecret] string? GitCloneCredentialsB64 = null,
    [property: ConstructSecret] string? ServeWebTokenB64 = null,
    [property: ConstructSecret] string? SudoPassword = null,
    [property: ConstructSecret] string? BackupKey = null);

/// <summary>
/// <c>POST /vms/{name}/jobs</c>. <paramref name="Kind"/> is <c>provision</c>, <c>reinstall</c> or <c>restore</c>.
/// A <c>reinstall</c> backs the VM up to its host, deletes and re-creates it with the settings it had
/// (<paramref name="FreshImage"/>: with new install media), provisions it with the stored settings and
/// restores the backup, all without a device. A <c>restore</c> restores the stored backup
/// <paramref name="BackupId"/> into the running VM; its key comes in <c>secrets.backupKey</c>. <paramref name="RequestId"/>
/// identifies the click: a repeat returns the job it started. <paramref name="Desired"/> replaces the
/// VM's stored settings; without it the stored ones are used. <paramref name="Commit"/> pins the
/// Construct source; the default is the engine's installed commit. <paramref name="DeferProjectCommands"/>
/// runs the project checkout and commands as a second phase (a run followed by a restore).
/// </summary>
public sealed record VmJobRequest(
    string Kind,
    string RequestId,
    string? Commit = null,
    VmDesiredState? Desired = null,
    bool? DeferProjectCommands = null,
    ProvisionSecretsRequest? Secrets = null,
    string? BackupId = null,
    bool? FreshImage = null);

/// <summary>The accepted job. <paramref name="Replayed"/> is true when the request id was seen before.</summary>
public sealed record VmJobAcceptedResponse(string JobId, string Kind, bool Replayed);

/// <summary>
/// <c>GET /vms/{name}/provision-status</c>: the three facts of design 7.5, each with what it is
/// computed from.
/// </summary>
/// <param name="SettingsChanged">A profile selection or setting changed since the last provision (a yellow dot on Reprovision).</param>
/// <param name="NewerPayload">The engine would provision a different Construct commit than the guest has (Reprovision becomes Update).</param>
/// <param name="HardwareChanged">A saved CPU or memory change waits for the next stop and start.</param>
public sealed record ProvisionStatusResponse(
    bool SettingsChanged,
    bool NewerPayload,
    bool HardwareChanged,
    string? DesiredHash,
    string? AvailableCommit,
    VmAppliedState? Applied,
    int? PendingCpu,
    int? PendingRamGb);

/// <summary>
/// <c>GET</c>/<c>PUT /vms/{name}/desired</c>: the VM's stored settings, which every provision job uses unless its
/// request carries its own. All fields are null while none are stored. <paramref name="Hash"/> is the value
/// <c>provision-status</c> compares with what the guest was last provisioned with.
/// </summary>
public sealed record VmDesiredStateResponse(VmDesiredState? Desired, string? Hash, DateTimeOffset? UpdatedAt, string? UpdatedBy)
{
    public static VmDesiredStateResponse From(StoredDesiredState? stored) =>
        new(stored?.Desired, stored?.Hash, stored?.UpdatedAt, stored?.UpdatedBy);
}

/// <summary>
/// <c>POST /jobs/{id}/retry</c>. Optional: the secrets the job needs again, as on <c>POST /vms/{name}/jobs</c>
/// (a restore's <c>backupKey</c> is required, the rest as before).
/// </summary>
public sealed record JobRetryRequest(ProvisionSecretsRequest? Secrets = null);

