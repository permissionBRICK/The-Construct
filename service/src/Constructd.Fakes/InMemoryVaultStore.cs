using System.Security.Cryptography;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Fakes;

/// <summary>The vault store for in-memory persistence: the same contract as SqliteVaultStore.</summary>
public sealed class InMemoryVaultStore : IVaultStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, VaultOwnerState> _owners = new(Ownership.NameComparer);
    private readonly Dictionary<string, Dictionary<string, VaultEntry>> _entries = new(Ownership.NameComparer);
    private readonly Dictionary<Type, Dictionary<string, IVaultRecord>> _records = [];
    private readonly Dictionary<string, VaultDevice> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<VaultEvent>> _events = new(Ownership.NameComparer);

    public Task<VaultOwnerState?> GetOwnerAsync(string owner, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_owners.GetValueOrDefault(owner));
    }

    public Task<IReadOnlyList<VaultOwnerState>> ListOwnersAsync(CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<VaultOwnerState>>(_owners.Values.ToArray());
    }

    public Task SaveSettingsAsync(string owner, string mode, string keyCheck, string? wrappedKey, CancellationToken ct)
    {
        lock (_gate)
        {
            var current = _owners.GetValueOrDefault(owner);
            _owners[owner] = new(current?.Owner ?? owner, current?.Revision ?? 0, mode, keyCheck, wrappedKey);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VaultEntry>> ListEntriesAsync(string owner, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<VaultEntry>>(_entries.TryGetValue(owner, out var all) ? all.Values.ToArray() : []);
    }

    public Task<long> SaveEntriesAsync(string owner, IReadOnlyList<VaultEntry> entries, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(owner, out var all)) _entries[owner] = all = new(StringComparer.Ordinal);
            foreach (var entry in entries) all[entry.Name] = entry;
            var current = _owners.GetValueOrDefault(owner) ?? new VaultOwnerState(owner, 0, null, null, null);
            _owners[owner] = current with { Revision = current.Revision + 1 };
            return Task.FromResult(current.Revision + 1);
        }
    }

    public Task<int> PruneTombstonesAsync(long beforeMs, CancellationToken ct)
    {
        var removed = 0;
        lock (_gate)
        {
            foreach (var all in _entries.Values)
                foreach (var stale in all.Values.Where(e => e.Deleted && e.UpdatedAt < beforeMs).ToArray())
                    removed += all.Remove(stale.Name) ? 1 : 0;
        }
        return Task.FromResult(removed);
    }

    public Task<IReadOnlyList<T>> ListAsync<T>(string? owner, CancellationToken ct) where T : class, IVaultRecord
    {
        lock (_gate)
        {
            var all = _records.TryGetValue(typeof(T), out var records) ? records.Values.OfType<T>() : [];
            return Task.FromResult<IReadOnlyList<T>>(all.Where(r => owner is null || Ownership.SameName(r.Owner, owner)).ToArray());
        }
    }

    public Task SaveAsync<T>(T record, CancellationToken ct) where T : class, IVaultRecord
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(typeof(T), out var records)) _records[typeof(T)] = records = new(StringComparer.Ordinal);
            records[record.Id] = record;
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string id, CancellationToken ct) where T : class, IVaultRecord
    {
        lock (_gate) if (_records.TryGetValue(typeof(T), out var records)) records.Remove(id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VaultDevice>> ListDevicesAsync(string owner, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<VaultDevice>>(_devices.Values.Where(d => Ownership.SameName(d.Owner, owner)).OrderBy(d => d.CreatedAt).ToArray());
    }

    public Task AddDeviceAsync(VaultDevice device, CancellationToken ct)
    {
        lock (_gate) _devices.Add(device.Id, device);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteDeviceAsync(string owner, string id, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_devices.TryGetValue(id, out var device) && Ownership.SameName(device.Owner, owner) && _devices.Remove(id));
    }

    public Task<VaultDevice?> FindDeviceAsync(string tokenHash, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_devices.Values.FirstOrDefault(d => TokenHasher.HashesEqual(d.TokenHash, tokenHash)));
    }

    public Task TouchDeviceAsync(string id, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate) if (_devices.TryGetValue(id, out var device)) _devices[id] = device with { LastUsedAt = at };
        return Task.CompletedTask;
    }

    public Task AppendEventAsync(string owner, VaultEvent entry, int keep, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_events.TryGetValue(owner, out var list)) _events[owner] = list = [];
            list.Add(entry);
            if (list.Count > keep) list.RemoveRange(0, list.Count - keep);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VaultEvent>> ListEventsAsync(string owner, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<VaultEvent>>(_events.TryGetValue(owner, out var list) ? list.ToArray() : []);
    }
}

/// <summary>
/// Wraps vault keys with a master key that lives in this process only: in-memory persistence forgets the
/// wrapped keys on restart anyway.
/// </summary>
public sealed class EphemeralVaultKeyProtector : IVaultKeyProtector
{
    private readonly byte[] _master = RandomNumberGenerator.GetBytes(VaultCrypto.KeyBytes);

    public string Wrap(ReadOnlySpan<byte> key) => VaultCrypto.Seal(_master, key, "vault-key");

    public byte[] Unwrap(string wrapped) => VaultCrypto.Open(_master, wrapped, "vault-key");
}
