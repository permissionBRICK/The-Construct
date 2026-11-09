using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

/// <summary>
/// Config backups and their per-<em>(owner, VM)</em> state (docs/plans/host-config-backups.md §3.6).
/// Rows are keyed by owner and VM name, never by a VM row, so they outlive the VM. Callers serialise
/// writes per pair (<c>BackupCoordinator</c>); the store only has to be consistent per call.
/// </summary>
public interface IBackupStore
{
    Task<ConfigBackup?> GetAsync(string id, CancellationToken ct);

    /// <summary>Every backup of the pair, open sessions included, newest first.</summary>
    Task<IReadOnlyList<ConfigBackup>> ListAsync(string owner, string vm, CancellationToken ct);

    /// <summary>Every backup on the host (cleanup).</summary>
    Task<IReadOnlyList<ConfigBackup>> ListAllAsync(CancellationToken ct);

    Task InsertAsync(ConfigBackup backup, CancellationToken ct);

    /// <summary>Stores the keys file of an open session; false when it is not open.</summary>
    Task<bool> SetKeysAsync(string id, string keysJson, CancellationToken ct);

    /// <summary>Marks an open session complete; false when it is not open.</summary>
    Task<bool> CompleteAsync(string id, DateTimeOffset completedAt, long sizeBytes, string sha256, DateTimeOffset? pinnedUntil, CancellationToken ct);

    /// <summary>Clears the pin of a complete backup; false when there is none.</summary>
    Task<bool> UnpinAsync(string id, CancellationToken ct);

    Task<bool> DeleteAsync(string id, CancellationToken ct);

    Task<BackupVmState?> GetVmAsync(string owner, string vm, CancellationToken ct);

    Task<IReadOnlyList<BackupVmState>> ListVmsAsync(CancellationToken ct);

    /// <summary>Inserts or replaces the pair's state row.</summary>
    Task SaveVmAsync(BackupVmState state, CancellationToken ct);

    Task DeleteVmAsync(string owner, string vm, CancellationToken ct);
}

/// <summary>A payload file found under the backup root, complete or still being written.</summary>
public sealed record BackupFileEntry(string Id, bool Partial, string Path, DateTimeOffset Modified);

/// <summary>
/// The encrypted payloads on disk: <c>&lt;root&gt;/&lt;first 16 hex of sha256(lowercase owner)&gt;/&lt;lowercase vm&gt;/&lt;id&gt;.age</c>,
/// <c>&lt;id&gt;.age.partial</c> while uploading.
/// </summary>
public interface IBackupFiles
{
    string Root { get; }

    /// <summary>Free bytes on the file system holding <see cref="Root"/>.</summary>
    long FreeBytes();

    /// <summary>Creates (or truncates) the partial file of a backup, creating its directories.</summary>
    Stream CreatePartial(string owner, string vm, string id);

    /// <summary>Renames the partial file to the final name.</summary>
    void Publish(string owner, string vm, string id);

    /// <summary>Opens the complete payload; null when it is missing.</summary>
    Stream? OpenRead(string owner, string vm, string id);

    /// <summary>Deletes the payload and any partial file of a backup.</summary>
    void Delete(string owner, string vm, string id);

    /// <summary>Removes the pair's directory when it is empty.</summary>
    void PruneDirectory(string owner, string vm);

    /// <summary>Every payload file under the root, for orphan cleanup.</summary>
    IReadOnlyList<BackupFileEntry> List();

    void DeleteFile(BackupFileEntry entry);
}

/// <summary>The free-space probe behind <see cref="IBackupFiles.FreeBytes"/>, replaceable in tests.</summary>
public interface IFreeSpaceProbe
{
    long FreeBytes(string path);
}
