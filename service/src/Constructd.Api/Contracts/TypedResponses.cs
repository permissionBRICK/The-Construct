using Constructd.Api.Infrastructure;
using Constructd.Core.Domain;

namespace Constructd.Api.Contracts;

// Response records that replace anonymous objects on the routes clients call, so the OpenAPI
// document (service/openapi/constructd.v1.json) has real schemas. Each one serializes to exactly
// the members the anonymous object had: same names, same nulls.

/// <summary><c>POST /vms</c>: the creation job was accepted.</summary>
public sealed record VmCreateAcceptedResponse(string JobId, bool Nested, bool NestedFromHostDefault, IReadOnlyList<string> IgnoredOptions);

/// <summary>The contract's shape of a VM create answer: 202 for a new job, 200 with <c>replayed</c> for a
/// repeated request with the same operation key. Only describes the two answers; nothing serializes it.</summary>
public sealed record VmCreateAnswer(string JobId, bool Nested, bool NestedFromHostDefault, IReadOnlyList<string> IgnoredOptions, bool? Replayed = null);

/// <summary>The contract's shape of a job start: 202 for a new job, 200 with <c>replayed</c> when the job
/// was already running. Only describes the two answers; nothing serializes it.</summary>
public sealed record JobStartAnswer(string? JobId, bool? Replayed = null);

/// <summary>A repeated request found the job it started earlier still running.</summary>
public sealed record JobReplayedResponse(string? JobId, bool Replayed);

public sealed record JobCancelResponse(string JobId, bool Cancelled);

/// <param name="RecommendedDiskGb">The disk size a create without <c>diskGb</c> gets (the cap of a disk that grows on demand).</param>
public sealed record VmDefaultsResponse(int HostLogicalCpus, int MaximumCpus, int RecommendedCpus, int MaximumRamGb, int RecommendedRamGb,
    int RecommendedDiskGb = Constructd.Api.Endpoints.VmEndpoints.DefaultDiskGb);

/// <param name="AppliesOn">Always <c>next-stop-start</c>.</param>
public sealed record VmCpuResponse(int CurrentCpus, int DesiredCpus, bool Pending, int? HostLogicalCpus, int? MaximumCpus,
    int? RecommendedCpus, IReadOnlyList<string> Warnings, string AppliesOn);

/// <param name="AppliesOn">Always <c>next-stop-start</c>.</param>
public sealed record VmMemoryResponse(int CurrentRamGb, int DesiredRamGb, bool Pending, int? MaximumRamGb, int? RecommendedRamGb,
    IReadOnlyList<string> Warnings, string AppliesOn);

/// <param name="AppliesOn">Always <c>next-stop-start</c>.</param>
public sealed record VmNestedResponse(bool Current, bool Desired, bool Pending, bool Available, bool Selectable, string AppliesOn);

public sealed record VmIdentityResponse(string VmName, VmKind Kind, VmTokenKind? TokenKind, string Owner, string? Parent,
    EffectiveAllowanceResponse? Delegation, string ServiceCommit, IReadOnlyList<string> ApiFeatures, CapacityMode CapacityMode);

/// <param name="VmToken">The VM's new credential, shown once.</param>
public sealed record VmTokenIssuedResponse([property: ConstructSecret] string VmToken, VmTokenKind Kind, DateTimeOffset IssuedAt);

public sealed record VmOverridesResponse(VmOverride? Stored, EffectiveAllowanceResponse Effective);

public sealed record UserAllowanceResponse(UserAllowance Stored, EffectiveAllowanceResponse Effective);

/// <summary>A user's API token as listed; the secret itself is never shown again.</summary>
public sealed record UserTokenSummaryResponse(string Id, string Label, DateTimeOffset Created, DateTimeOffset? LastUsed);

/// <param name="Kind">The host's platform, typed (<paramref name="Backend"/> is the child driver's own name for it).</param>
public sealed record HostCapabilitiesResponse(string Backend, HostKind Kind, BackendCapabilities Capabilities, HostNestedCapabilities Nested, HostPolicyResponse Policy);

public sealed record HostNestedCapabilities(bool Available, bool Default, bool Selectable);

public sealed record HostPolicyResponse(bool HostForwardsEnabled, bool DirectAddressReporting, string DefaultMode, bool OwnerMaySwitchMode,
    bool AllowNeverLifetime, long? MaxChildLifetimeSeconds, CapacityMode CapacityMode, MemoryPressureConfig MemoryPressure, HostHttpsPolicy Https);

/// <param name="MainAddress">Whether a main forward gets a port-less address under the VM's own name.</param>
public sealed record HostHttpsPolicy(bool Available, int? SharedPort, bool MainAddress);

/// <summary>
/// <c>GET /health</c>. The handler writes a dictionary, because <c>commit</c>, <c>packageVersion</c> and
/// <c>installedAt</c> are present only for an authenticated caller (and then may be null); this record
/// describes that shape for the contract.
/// </summary>
public sealed record HealthResponse(string Status, int SchemaVersion, int SchemaMinReadableBy, IReadOnlyList<string> ApiFeatures,
    HealthMaintenance? Maintenance, string? Commit = null, string? PackageVersion = null, DateTimeOffset? InstalledAt = null);

public sealed record HealthMaintenance(MaintenanceState Phase, DateTimeOffset? Since, string? UpdateId, int RetryAfterSeconds);

// ── key vault (docs/plans/key-vault-hosted.md). Times in these records are Unix milliseconds. ──

public sealed record VaultEntriesResponse(long Revision, string? Mode, string? KeyCheck, IReadOnlyList<string> UnlockedVms,
    IReadOnlyList<VaultEntryResponse> Entries, IReadOnlyList<VaultDeviceResponse> Devices);

/// <param name="Payload">The entry's value, encrypted with the vault key.</param>
public sealed record VaultEntryResponse(string Name, string Description, bool HasUsername, [property: ConstructSecret] string? Payload,
    long UpdatedAt, string UpdatedBy, bool Deleted, bool AutoApprove);

public sealed record VaultDeviceResponse(string Id, string Label, long CreatedAt, long? LastUsedAt)
{
    public static VaultDeviceResponse From(VaultDevice d) =>
        new(d.Id, d.Label, d.CreatedAt.ToUnixTimeMilliseconds(), d.LastUsedAt?.ToUnixTimeMilliseconds());
}

public sealed record VaultPutEntriesResponse(long Revision, int Applied);

public sealed record VaultLeasesResponse(IReadOnlyList<VaultLeaseResponse> Leases);

public sealed record VaultLeaseResponse(string Id, string Vm, string Name, int? UsesLeft, long ExpiresAt, string Reason, string Origin);

/// <param name="WebUrlSource">Where <paramref name="WebUrl"/> comes from: the host setting, the service option or the service's own address.</param>
public sealed record VaultDevicesResponse(string WebUrl, string WebUrlSource, IReadOnlyList<VaultDeviceResponse> Devices);

/// <param name="Token">The device's credential, shown once.</param>
public sealed record VaultDevicePairedResponse(string Id, [property: ConstructSecret] string Token, string WebUrl, string WebUrlSource);

public sealed record VaultActivityResponse(IReadOnlyList<VaultActivityEvent> Events);

public sealed record VaultActivityEvent(long At, string Vm, string Text, bool Warning);

public sealed record VaultApprovalsResponse(IReadOnlyList<Constructd.Core.Services.VaultApprovalView> Approvals);

public sealed record VaultFilesResponse(IReadOnlyList<Constructd.Core.Services.VaultFileView> Files);

/// <param name="Mode"><c>available</c>, <c>locked</c>, or null while a Companion vault is not set up.</param>
/// <param name="HoldsKey">Whether this engine holds <c>K</c> right now: granted (<c>available</c>) or in RAM for an unlocked VM.</param>
/// <param name="Records">The vault lives in <c>secret</c> records (an account in the new stack); else in the Companion's table.</param>
public sealed record VaultModeResponse(string? Mode, bool HoldsKey, bool Records, IReadOnlyList<string> UnlockedVms);

public sealed record VaultEntryRequestsResponse(IReadOnlyList<Constructd.Core.Services.VaultEntryRequestView> Requests);

// ── config backups (docs/plans/host-config-backups.md) ──

public sealed record BackupListResponse(IReadOnlyList<BackupSummaryResponse> Backups);

/// <param name="Trigger">The stored trigger: <c>scheduled</c>, <c>manual</c>, <c>pre-reinstall</c> or <c>slot</c>.</param>
/// <param name="Kind"><c>slot</c> (the VM's own upload), <c>scheduled</c>, <c>requested</c> or <c>pre-reinstall</c> (host-requested).</param>
/// <param name="Pinned">Pinned now (<c>pinnedUntil</c> in the future).</param>
/// <param name="Protected">The backup the retention rule keeps; deleting it needs <c>?confirm=true</c>.</param>
/// <param name="FileCount">How many files the export holds, when the guest said.</param>
public sealed record BackupSummaryResponse(string Id, DateTimeOffset CreatedAt, string Trigger, long? SizeBytes, string? Sha256, DateTimeOffset? PinnedUntil,
    string Kind, bool Pinned, bool Protected, long? FileCount);

/// <summary><c>POST /vms/{name}/backups</c>: the session opened, what it counts as and the most it may hold.</summary>
public sealed record BackupCreatedResponse(string Id, long MaxBytes, string Kind);

/// <summary><c>PUT /vms/{name}/backups/{id}/content</c>: the stored payload.</summary>
public sealed record BackupUploadedResponse(string Id, long SizeBytes, string Sha256, DateTimeOffset CreatedAt, string Kind);

public sealed record BackupStatusResponse(bool Enabled, DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt, BackupFailure? LastFailure,
    DateTimeOffset? FailingSince, bool RequestPending, int Count, long TotalBytes, DateTimeOffset? NextDueAt);

/// <param name="Trigger">The open request's trigger: <c>manual</c> or <c>pre-reinstall</c>.</param>
public sealed record BackupRequestedResponse(bool RequestPending, string Trigger);

/// <param name="IntervalHours">The VM's own override; null = the host default.</param>
/// <param name="Keep">The VM's own override; null = the host default.</param>
public sealed record BackupSettingsResponse(int? IntervalHours, int? Keep, BackupEffectiveSettings Effective, BackupHostSettings Host);

public sealed record BackupEffectiveSettings(bool Enabled, int IntervalHours, int Keep);

public sealed record BackupHostSettings(bool Enabled, int IntervalHours, int Keep, int MaxKeep);

// ── HTTPS forwards (docs/https-forwards.md) ──

public sealed record HttpsStatusResponse(Constructd.Api.Https.HttpsCertificateInfo? Certificate, string Directory, HttpsConfig Config,
    HttpsNamesResponse Names, IReadOnlyList<Constructd.Api.Https.HttpsListenerStatus> Listeners, IReadOnlyList<string> Warnings);

/// <param name="PublicHostCovered">Null when no certificate is installed.</param>
public sealed record HttpsNamesResponse(string PublicHost, bool? PublicHostCovered, string? PublicHostPattern, string Example, bool? ExampleCovered,
    string? VaultHost, bool? VaultHostCovered);

// ── media ──

public sealed record MediaReferenceResponse(string VmName, MediaSlot Slot, DateTimeOffset Created);
