using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Api.Contracts;

/// <summary>
/// <c>POST /host/updates/run</c> (12.1). <paramref name="State"/>: <c>started</c> (202, follow the job) or
/// <c>up-to-date</c> (200, the host runs its channel's release).
/// </summary>
public sealed record HostUpdateRunResponse(string State, string? JobId, string? UpdateId);

/// <summary>The release the host's channel offers. <paramref name="Version"/> is <c>X.Y.Z+&lt;commit&gt;</c>.</summary>
public sealed record HostUpdateOfferResponse(string Version, string Commit);

/// <summary>
/// <c>GET /host/updates/status</c>. The first six members are the answer old clients read; the rest is what one
/// Update button needs: <paramref name="Version"/> (this host's build), <paramref name="Channel"/>,
/// <paramref name="Auto"/> (<c>updates.auto</c>), <paramref name="Available"/> (the channel's release when this
/// host does not run it; <paramref name="AvailableError"/> when the channel could not be read) and
/// <paramref name="DatabaseCopy"/>: the database copy the last update took, kept for a manual restore.
/// The state and error of the last run are <c>current.state</c> and <c>current.error</c>.
/// </summary>
public sealed record HostUpdateStatusResponse(bool SupportsAutoApply, InstalledRelease Installed, object? Current, IEnumerable<object> History,
    RecoveryRecord? RecoveryRecord, object? LatestKnown, string Version, string Channel, bool Auto, HostUpdateOfferResponse? Available,
    string? AvailableError, string? DatabaseCopy);

/// <summary>A user's update settings (<c>GET</c>, <c>PUT /me/updates</c>). <paramref name="AutoReprovision"/>: after
/// this host updated itself, it reprovisions the user's VMs on it (<c>updates.autoReprovision</c>, off by default).</summary>
public sealed record UserUpdateSettingsBody(bool AutoReprovision);

/// <summary><c>POST /me/vms/reprovision</c>, <c>POST /host/vms/reprovision</c>: the VMs reprovisioned one after the
/// other, in this order. Each gets an ordinary <c>provision</c> job (see <c>GET /jobs</c>).</summary>
public sealed record VmReprovisionResponse(IReadOnlyList<string> Vms);

/// <summary>
/// <c>GET /vms/{name}/update</c>: <paramref name="NewerPayload"/> when the engine would provision a different
/// Construct commit than the VM has; <paramref name="HostUpdate"/> only when the VM's owner is an admin of this
/// host and its channel offers a release it does not run.
/// </summary>
public sealed record VmUpdateStatusResponse(bool NewerPayload, string? AvailableCommit, string? AppliedCommit, HostUpdateOfferResponse? HostUpdate);

/// <summary>
/// <c>POST /vms/{name}/update</c>: either the VM's <c>provision</c> job (<paramref name="JobId"/>), or, for an
/// admin's VM while a host release is out, the host update (<paramref name="HostUpdateId"/>,
/// <paramref name="HostJobId"/>), after which the host reprovisions this VM.
/// </summary>
public sealed record VmUpdateResponse(string? JobId, string? HostUpdateId, string? HostJobId);
