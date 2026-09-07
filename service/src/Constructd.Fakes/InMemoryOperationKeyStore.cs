using Constructd.Core.Abstractions;
namespace Constructd.Fakes;

public sealed partial class InMemoryOperationKeyStore : IOperationKeyStore
{
    private readonly Dictionary<(string Owner, string Kind, string Key), OperationKeyRecord> _keys = new();
    private static (string, string, string) Key(string owner, string kind, string key) => (owner.ToUpperInvariant(), kind, key);
    public Task<OperationKeyRecord?> GetAsync(string owner, string kind, string key, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            return Task.FromResult(_keys.GetValueOrDefault(Key(owner, kind, key)));
        }
    }
    public Task<(OperationKeyOutcome Outcome, OperationKeyRecord? Existing)> TryInsertAsync(OperationKeyRecord record, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            {
                var key = Key(record.Owner, record.Kind, record.Key);
                if (_keys.TryGetValue(key, out var old)) return Task.FromResult<(OperationKeyOutcome, OperationKeyRecord?)>((old.Fingerprint == record.Fingerprint && old.Target == record.Target ? OperationKeyOutcome.Replay : OperationKeyOutcome.Conflict, old));
                _keys.Add(key, record); return Task.FromResult<(OperationKeyOutcome, OperationKeyRecord?)>((OperationKeyOutcome.Inserted, null));
            }

        }
    }
    public Task<bool> CompleteAsync(string owner, string kind, string key, string responseJson, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            {
                var k = Key(owner, kind, key); if (!_keys.TryGetValue(k, out var old) || old.State != OperationKeyState.InFlight) return Task.FromResult(false);
                _keys[k] = old with { State = OperationKeyState.Completed, ResponseJson = responseJson }; return Task.FromResult(true);
            }

        }
    }
    public Task<bool> RemoveAsync(string owner, string kind, string key, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            return Task.FromResult(_keys.Remove(Key(owner, kind, key)));
        }
    }
    public Task<int> SweepAsync(DateTimeOffset olderThan, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            { var keys = _keys.Where(x => x.Value.State == OperationKeyState.Completed && x.Value.Created < olderThan).Select(x => x.Key).ToArray(); foreach (var k in keys) _keys.Remove(k); return Task.FromResult(keys.Length); }
        }
    }
}
