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
public sealed record ProvisionSecretsRequest(
    [property: ConstructSecret] string? VmTokenB64 = null,
    [property: ConstructSecret] string? GitCloneCredentialsB64 = null,
    [property: ConstructSecret] string? ServeWebTokenB64 = null,
    [property: ConstructSecret] string? SudoPassword = null);

/// <summary>
/// <c>POST /vms/{name}/jobs</c>. <paramref name="Kind"/> is <c>provision</c>. <paramref name="RequestId"/>
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
    ProvisionSecretsRequest? Secrets = null);

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
