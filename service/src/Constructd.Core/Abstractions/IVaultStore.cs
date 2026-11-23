using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

/// <summary>
/// Durable state of the hosted key vaults. Only <see cref="Services.VaultHostService"/> writes it, under
/// its own gate, so the store needs no cross-call transactions. Owner names compare
/// case-insensitively, secret names ordinally.
/// </summary>
public interface IVaultStore
{
    Task<VaultOwnerState?> GetOwnerAsync(string owner, CancellationToken ct);

    Task<IReadOnlyList<VaultOwnerState>> ListOwnersAsync(CancellationToken ct);

    /// <summary>Creates the vault row when needed; the revision is kept.</summary>
    Task SaveSettingsAsync(string owner, string mode, string keyCheck, string? wrappedKey, CancellationToken ct);

    Task<IReadOnlyList<VaultEntry>> ListEntriesAsync(string owner, CancellationToken ct);

    /// <summary>Writes the entries (replacing the same names) and bumps the revision once; returns it.</summary>
    Task<long> SaveEntriesAsync(string owner, IReadOnlyList<VaultEntry> entries, CancellationToken ct);

    /// <summary>Removes tombstones last updated before <paramref name="beforeMs"/>.</summary>
    Task<int> PruneTombstonesAsync(long beforeMs, CancellationToken ct);

    /// <summary>Leases, cleanups, scrub jobs or file decisions; all of them when owner is null.</summary>
    Task<IReadOnlyList<T>> ListAsync<T>(string? owner, CancellationToken ct) where T : class, IVaultRecord;

    Task SaveAsync<T>(T record, CancellationToken ct) where T : class, IVaultRecord;

    Task DeleteAsync<T>(string id, CancellationToken ct) where T : class, IVaultRecord;

    Task<IReadOnlyList<VaultDevice>> ListDevicesAsync(string owner, CancellationToken ct);

    Task AddDeviceAsync(VaultDevice device, CancellationToken ct);

    Task<bool> DeleteDeviceAsync(string owner, string id, CancellationToken ct);

    Task<VaultDevice?> FindDeviceAsync(string tokenHash, CancellationToken ct);

    Task TouchDeviceAsync(string id, DateTimeOffset at, CancellationToken ct);

    /// <summary>Appends an event and keeps only the newest <paramref name="keep"/> of that owner.</summary>
    Task AppendEventAsync(string owner, VaultEvent entry, int keep, CancellationToken ct);

    /// <summary>Oldest first.</summary>
    Task<IReadOnlyList<VaultEvent>> ListEventsAsync(string owner, CancellationToken ct);
}

/// <summary>
/// Wraps a user's vault key with the host master key (DPAPI LocalMachine on Windows, a root-only file
/// elsewhere). Only used in always-available mode.
/// </summary>
public interface IVaultKeyProtector
{
    string Wrap(ReadOnlySpan<byte> key);

    /// <summary>Throws <see cref="System.Security.Cryptography.CryptographicException"/> on tampering.</summary>
    byte[] Unwrap(string wrapped);
}

/// <summary>The hook power, idle and deletion paths use to drop a VM's unlocked vault key.</summary>
public interface IVaultUnlocks
{
    void Drop(string vmName);
}

/// <summary>A key sealed to this engine's host key by a device (plan 8.7): <c>K</c> for a mode switch or a VM unlock.</summary>
public sealed record VaultKeyGrant(long Epoch, byte[] Sealed)
{
    public override string ToString() => $"VaultKeyGrant {{ Epoch = {Epoch} }}";
}

/// <summary>
/// The vault of an account in the new stack (plan 9.1, 9.6): <c>secret</c> records in this engine's store, sealed
/// with <c>D</c>, each carrying today's entry with its payload sealed with <c>K</c>. An account is in the new stack
/// here once a device granted this engine <c>D</c>; every other user keeps the <see cref="IVaultStore"/> table.
/// </summary>
public interface IVaultRecords
{
    /// <summary>Whether <paramref name="owner"/>'s vault lives in records on this engine.</summary>
    bool Hosts(string owner);

    /// <summary>Every account whose vault lives in records here.</summary>
    IReadOnlyList<string> Owners();

    /// <summary><c>K</c> when a device granted it to this engine (<c>available</c> mode), else null. Callers zero it.</summary>
    byte[]? GrantedKey(string owner);

    /// <summary>Opens <c>K</c> sealed to this engine's host key; throws a <see cref="System.Security.Cryptography.CryptographicException"/> otherwise.</summary>
    byte[] OpenKey(string owner, VaultKeyGrant grant);

    /// <summary><c>available</c> keeps <paramref name="grant"/> (<c>K</c> sealed to the host key), <c>locked</c> withdraws <c>K</c>.</summary>
    void SetMode(string owner, bool available, VaultKeyGrant? grant);

    /// <summary>Every <c>secret</c> record, tombstones included, with its revision and provenance.</summary>
    Task<IReadOnlyList<VaultEntry>> ListAsync(string owner, CancellationToken ct);

    /// <summary>
    /// Writes one entry (a tombstone when <see cref="VaultEntry.Deleted"/>) on <paramref name="baseRevision"/>;
    /// false when the base was stale: the caller re-reads and re-applies its one change (9.6).
    /// </summary>
    Task<bool> WriteAsync(string owner, VaultEntry entry, long baseRevision, VaultProvenance provenance, IReadOnlyList<string> origins, CancellationToken ct);
}
