using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Fakes;

public sealed partial class InMemoryOperationKeyStore(InMemoryJobStore? jobs = null, IClock? clock = null) : IOperationKeyStore
{
    private readonly Dictionary<(string Owner, string Kind, string Key), OperationKeyRecord> _keys = new();
    private readonly Dictionary<(string Owner, string Kind, string Key), DateTimeOffset> _completed = new();
    private static (string, string, string) Key(string owner, string kind, string key) => (owner.ToUpperInvariant(), kind, key);
    public Task<OperationKeyRecord?> GetAsync(string owner, string kind, string key, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_keys.GetValueOrDefault(Key(owner, kind, key)));
        }
    }
    public Task<(OperationKeyOutcome Outcome, OperationKeyRecord? Existing)> TryInsertAsync(OperationKeyRecord record, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            ct.ThrowIfCancellationRequested();
            {
                var key = Key(record.Owner, record.Kind, record.Key);
                if (_keys.TryGetValue(key, out var old)) return Task.FromResult<(OperationKeyOutcome, OperationKeyRecord?)>((old.Fingerprint == record.Fingerprint && StringComparer.OrdinalIgnoreCase.Equals(old.Target, record.Target) ? OperationKeyOutcome.Replay : OperationKeyOutcome.Conflict, old));
                _keys.Add(key, record); if (record.State == OperationKeyState.Completed) _completed[key] = record.Created; return Task.FromResult<(OperationKeyOutcome, OperationKeyRecord?)>((OperationKeyOutcome.Inserted, null));
            }

        }
    }
    public Task<bool> CompleteAsync(string owner, string kind, string key, string responseJson, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            ct.ThrowIfCancellationRequested();
            {
                var k = Key(owner, kind, key); if (!_keys.TryGetValue(k, out var old) || old.State != OperationKeyState.InFlight) return Task.FromResult(false);
                _completed[k] = clock?.UtcNow ?? DateTimeOffset.UtcNow;
                _keys[k] = old with { State = OperationKeyState.Completed, ResponseJson = responseJson }; return Task.FromResult(true);
            }

        }
    }
    public Task<bool> RemoveAsync(string owner, string kind, string key, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            ct.ThrowIfCancellationRequested();
            _completed.Remove(Key(owner, kind, key));
            return Task.FromResult(_keys.Remove(Key(owner, kind, key)));
        }
    }
    public Task<int> SweepAsync(DateTimeOffset olderThan, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            ct.ThrowIfCancellationRequested();
            { var keys = _keys.Where(x => x.Value.State == OperationKeyState.Completed && _completed.TryGetValue(x.Key, out var completed) && completed < olderThan && (x.Value.JobId is null || jobs?.GetAsync(x.Value.JobId, ct).GetAwaiter().GetResult()?.State is JobState.Succeeded or JobState.Failed or JobState.Cancelled)).Select(x => x.Key).ToArray(); foreach (var k in keys) { _keys.Remove(k); _completed.Remove(k); } return Task.FromResult(keys.Length); }
        }
    }
}
