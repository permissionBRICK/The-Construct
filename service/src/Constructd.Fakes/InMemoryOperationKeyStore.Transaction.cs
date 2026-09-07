namespace Constructd.Fakes;

public sealed partial class InMemoryOperationKeyStore
{
    internal Action SnapshotForRollback()
    {
        var keys = _keys.ToArray();
        var completed = _completed.ToArray();
        return () =>
        {
            _keys.Clear();
            _completed.Clear();
            foreach (var pair in completed) _completed.Add(pair.Key, pair.Value);
            foreach (var pair in keys) _keys.Add(pair.Key, pair.Value);
        };
    }
}
