using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Fakes;

public sealed class InMemoryBackupStore : IBackupStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ConfigBackup> _backups = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Owner, string Vm), BackupVmState> _vms = [];

    private static (string, string) Key(string owner, string vm) => (owner.ToUpperInvariant(), vm.ToUpperInvariant());
    private static IReadOnlyList<ConfigBackup> Newest(IEnumerable<ConfigBackup> rows) =>
        rows.OrderByDescending(b => b.CreatedAt).ThenByDescending(b => b.Id, StringComparer.Ordinal).ToArray();

    public Task<ConfigBackup?> GetAsync(string id, CancellationToken ct)
    { lock (_gate) return Task.FromResult(_backups.GetValueOrDefault(id)); }

    public Task<IReadOnlyList<ConfigBackup>> ListAsync(string owner, string vm, CancellationToken ct)
    { lock (_gate) return Task.FromResult(Newest(_backups.Values.Where(b => Ownership.SameName(b.Owner, owner) && Ownership.SameName(b.Vm, vm)))); }

    public Task<IReadOnlyList<ConfigBackup>> ListAllAsync(CancellationToken ct)
    { lock (_gate) return Task.FromResult(Newest(_backups.Values)); }

    public Task InsertAsync(ConfigBackup backup, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_backups.TryAdd(backup.Id, backup)) throw new InvalidOperationException("Duplicate backup id.");
            return Task.CompletedTask;
        }
    }

    public Task<bool> SetKeysAsync(string id, string keysJson, CancellationToken ct) =>
        Update(id, b => b.IsOpen ? b with { KeysJson = keysJson } : null);

    public Task<bool> CompleteAsync(string id, DateTimeOffset completedAt, long sizeBytes, string sha256, DateTimeOffset? pinnedUntil, CancellationToken ct) =>
        Update(id, b => b.IsOpen ? b with { State = BackupStates.Complete, CompletedAt = completedAt, SizeBytes = sizeBytes, Sha256 = sha256, PinnedUntil = pinnedUntil } : null);

    public Task<bool> SetPinAsync(string id, DateTimeOffset? pinnedUntil, CancellationToken ct) =>
        Update(id, b => b.IsComplete ? b with { PinnedUntil = pinnedUntil } : null);

    public Task<bool> DeleteAsync(string id, CancellationToken ct)
    { lock (_gate) return Task.FromResult(_backups.Remove(id)); }

    public Task<BackupVmState?> GetVmAsync(string owner, string vm, CancellationToken ct)
    { lock (_gate) return Task.FromResult(_vms.GetValueOrDefault(Key(owner, vm))); }

    public Task<IReadOnlyList<BackupVmState>> ListVmsAsync(CancellationToken ct)
    { lock (_gate) return Task.FromResult<IReadOnlyList<BackupVmState>>(_vms.Values.ToArray()); }

    public Task SaveVmAsync(BackupVmState state, CancellationToken ct)
    {
        lock (_gate)
        {
            // The first spelling of the pair stays, as the SQLite row's key does.
            var key = Key(state.Owner, state.Vm);
            _vms[key] = _vms.TryGetValue(key, out var existing) ? state with { Owner = existing.Owner, Vm = existing.Vm } : state;
            return Task.CompletedTask;
        }
    }

    public Task DeleteVmAsync(string owner, string vm, CancellationToken ct)
    { lock (_gate) { _vms.Remove(Key(owner, vm)); return Task.CompletedTask; } }

    private Task<bool> Update(string id, Func<ConfigBackup, ConfigBackup?> change)
    {
        lock (_gate)
        {
            if (!_backups.TryGetValue(id, out var current) || change(current) is not { } next) return Task.FromResult(false);
            _backups[id] = next;
            return Task.FromResult(true);
        }
    }
}
