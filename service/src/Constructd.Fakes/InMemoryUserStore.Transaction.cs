namespace Constructd.Fakes;

public sealed partial class InMemoryUserStore
{
    internal Action SnapshotForRollback()
    {
        var users = _users.ToArray();
        return () =>
        {
            _users.Clear();
            foreach (var pair in users) _users.TryAdd(pair.Key, pair.Value);
        };
    }
}
