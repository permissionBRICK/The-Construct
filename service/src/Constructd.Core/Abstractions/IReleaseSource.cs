using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

public sealed record ReleaseAsset(string Name, Uri Url, long SizeBytes);
public sealed record ReleaseDescriptor(string Tag, string Commit, DateTimeOffset PublishedAt, IReadOnlyList<ReleaseAsset> Assets);
public sealed record SourceAssetDescriptor(string Commit, string Tag, Uri Url, long SizeBytes, string Sha256);
public interface IReleaseSource
{
    Task<IReadOnlyList<ReleaseDescriptor>> ListHostReleasesAsync(string repository, CancellationToken ct, string? releaseTag = null);
    Task<SourceAssetDescriptor> GetSourceAssetAsync(string repository, string commit, CancellationToken ct);
    Task DownloadAsync(ReleaseAsset asset, string destinationPath, IProgress<string>? progress, CancellationToken ct);
}

public sealed record ManifestDatabase(int SchemaVersion, int MinReadableBy, IReadOnlyList<int> BreakingMigrations);
public sealed record ManifestCompat(string MinInstalledCommitDate, int MinSchemaVersionToUpdateFrom);
/// <summary>Configuration compatibility (§11.2): appsettings is additive and never rewritten; a release that needs a new mandatory key lists it.</summary>
public sealed record ManifestConfig(int SettingsSchemaVersion, int MinReadableBy, IReadOnlyList<string> RequiredKeys, IReadOnlyList<string> NewKeysWithDefaults);
public sealed record SharedRuntime(string Name, int MajorVersion);
public sealed record ReleaseManifest(
    int SchemaVersion,
    string Commit,
    string Ref,
    string PackageVersion,
    DateTimeOffset BuiltAt,
    string Repository,
    string ReleaseTag,
    string PayloadAsset,
    string PayloadSha256,
    string SumsSha256,
    string UpdaterPath,
    string UpdaterSha256,
    ManifestDatabase Database,
    ManifestConfig Config,
    ManifestCompat Compat,
    long? PayloadSizeBytes = null,
    long? PayloadUncompressedSizeBytes = null,
    string? FrameworkDependentAsset = null,
    string? FrameworkDependentSha256 = null,
    long? FrameworkDependentSizeBytes = null,
    string? FrameworkDependentSumsSha256 = null,
    long? FrameworkDependentUncompressedSizeBytes = null,
    IReadOnlyList<SharedRuntime>? Runtimes = null);
public sealed record StagedUpdate(string UpdateId, ReleaseManifest Manifest, string StagedPath, IReadOnlyList<string> Files, string Source = "self-contained");
public interface IUpdateStager
{
    Task<StagedUpdate> StageAsync(string updateId, ReleaseDescriptor release, IProgress<string>? progress, CancellationToken ct);
    Task<bool> VerifyStagedAsync(StagedUpdate staged, CancellationToken ct);
    Task RemoveStagedAsync(string updateId, CancellationToken ct);
}

/// <param name="HealthToken">Random secret written only into the SYSTEM-only handoff file; the new binary accepts it on loopback /health for the full body.</param>
public sealed record UpdateHandoff(string UpdateId, string Commit, string StagedPath, string PublishDir, string ScriptsDir, string DataDir, string ServiceName, string PreviousCommit, string HealthUrl, string CertificateThumbprint, string AdminCliPath, string HealthToken, DateTimeOffset WrittenAt, int PreviousSchemaVersion = 0, int HealthTimeoutSeconds = 120);
/// <summary>What the fence permits, scoped to ONE update id (§11.7). Checked by the updater under updater.lock before stop, replace and rollback.</summary>
public enum FenceDisposition
{
    /// <summary>The update is over before replacement; no rollback authority remains; only a fresh apply may follow.</summary>
    Closed,
    /// <summary>The new binary is kept; the updater may only finish the commit phase; rollback is refused.</summary>
    CommitOnly,
    /// <summary>An admin authorized rollback; the service stays in maintenance until the rollback outcome is recorded.</summary>
    RollbackAuthorized,
}
public sealed record UpdateFence(string UpdateId, FenceDisposition Disposition, string Actor, DateTimeOffset At);
public interface IUpdaterLauncher
{
    /// <summary>Writes the handoff durably, THEN creates and starts the one-shot SYSTEM task (§11.5).</summary>
    Task LaunchAsync(UpdateHandoff handoff, CancellationToken ct);
    Task PrepareAsync(UpdateHandoff handoff, CancellationToken ct) => Task.CompletedTask;
    Task ResumeAsync(UpdateHandoff handoff, CancellationToken ct) => LaunchAsync(handoff, ct);
    Task<UpdateFence?> ReadFenceAsync(CancellationToken ct) => Task.FromResult<UpdateFence?>(null);
    Task<UpdateHandoff?> ReadHandoffAsync(CancellationToken ct);
    /// <summary>The updater's last-update.json, or null.</summary>
    Task<RecoveryRecord?> ReadRecoveryRecordAsync(CancellationToken ct);
    /// <summary>Writes the fence (temp + rename) while holding the updater lock; false when the updater still holds it.</summary>
    Task<bool> TryWriteFenceAsync(UpdateFence fence, CancellationToken ct);
    Task<UpdateHandoff?> ReadOwnHandoffAsync(string expectedCommit, CancellationToken ct);
}
public sealed record RecoveryRecord(string UpdateId, string Commit, string PreviousCommit, string Phase, DateTimeOffset PhaseAt, string? Outcome, string? Error, string BackupPath, bool BackupComplete, bool ReplaceStarted, string StagedPath, int HealthAttempts, IReadOnlyList<string> ManualSteps);

public interface IHostUpdateStore
{
    Task<HostUpdateRecord?> GetAsync(string id, CancellationToken ct);
    Task<HostUpdateRecord?> GetActiveAsync(CancellationToken ct);
    Task<IReadOnlyList<HostUpdateRecord>> ListAsync(int limit, CancellationToken ct);
    /// <summary>Insert only when no non-terminal row exists (409 update-in-progress otherwise).</summary>
    Task<bool> TryStartAsync(HostUpdateRecord record, CancellationToken ct);
    Task UpsertAsync(HostUpdateRecord record, CancellationToken ct);
}
