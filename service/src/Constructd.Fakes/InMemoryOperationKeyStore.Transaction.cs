namespace Constructd.Fakes;

public sealed partial class InMemoryOperationKeyStore
{
    internal Action SnapshotForRollback()
    {
        var keys = _keys.ToArray();
        return () =>
        {
            _keys.Clear();
            foreach (var pair in keys) _keys.Add(pair.Key, pair.Value);
        };
    }
}
