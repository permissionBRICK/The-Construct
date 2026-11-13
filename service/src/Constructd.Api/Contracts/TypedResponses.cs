using Constructd.Api.Infrastructure;
using Constructd.Core.Domain;

namespace Constructd.Api.Contracts;

// Response records that replace anonymous objects on the routes clients call, so the OpenAPI
// document (service/openapi/constructd.v1.json) has real schemas. Each one serializes to exactly
// the members the anonymous object had: same names, same nulls.

/// <summary><c>POST /vms</c>: the creation job was accepted.</summary>
public sealed record VmCreateAcceptedResponse(string JobId, bool Nested, bool NestedFromHostDefault, IReadOnlyList<string> IgnoredOptions);

/// <summary>A repeated request found the job it started earlier still running.</summary>
public sealed record JobReplayedResponse(string? JobId, bool Replayed);

public sealed record JobCancelResponse(string JobId, bool Cancelled);

public sealed record VmDefaultsResponse(int HostLogicalCpus, int MaximumCpus, int RecommendedCpus, int MaximumRamGb, int RecommendedRamGb);

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

public sealed record HostCapabilitiesResponse(string Backend, BackendCapabilities Capabilities, HostNestedCapabilities Nested, HostPolicyResponse Policy);

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

// ── config backups (docs/plans/host-config-backups.md) ──

public sealed record BackupListResponse(IReadOnlyList<BackupSummaryResponse> Backups);

public sealed record BackupSummaryResponse(string Id, DateTimeOffset CreatedAt, string Trigger, long? SizeBytes, string? Sha256, DateTimeOffset? PinnedUntil);

public sealed record BackupStatusResponse(bool Enabled, DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt, BackupFailure? LastFailure,
    DateTimeOffset? FailingSince, bool RequestPending, int Count, long TotalBytes, DateTimeOffset? NextDueAt);

public sealed record BackupRequestedResponse(bool RequestPending);

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
