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
