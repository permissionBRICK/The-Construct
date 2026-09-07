namespace Constructd.Fakes;

public sealed partial class InMemoryJobStore
{
    internal Action SnapshotForRollback()
    {
        var jobs = _jobs.ToArray();
        return () =>
        {
            _jobs.Clear();
            foreach (var pair in jobs) _jobs.TryAdd(pair.Key, pair.Value);
        };
    }
}
