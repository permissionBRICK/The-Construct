namespace Constructd.Fakes;

public sealed partial class InMemoryVmRepository
{
    internal Action SnapshotForRollback()
    {
        var vms = _vms.ToArray();
        var activity = _activity.ToArray();
        var overrides = _overrides.ToArray();
        var cascades = _cascades.ToArray();
        return () =>
        {
            _vms.Clear();
            foreach (var pair in vms) _vms.TryAdd(pair.Key, pair.Value);
            _activity.Clear();
            foreach (var pair in activity) _activity.TryAdd(pair.Key, pair.Value);
            _overrides.Clear();
            foreach (var pair in overrides) _overrides.Add(pair.Key, pair.Value);
            _cascades.Clear();
            foreach (var pair in cascades) _cascades.Add(pair.Key, pair.Value);
        };
    }
}
