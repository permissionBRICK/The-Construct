namespace Constructd.Fakes;

public sealed partial class InMemoryCapacityLedger
{
    internal Action SnapshotForRollback()
    {
        var reservations = _reservations.ToArray();
        return () =>
        {
            _reservations.Clear();
            foreach (var pair in reservations) _reservations.Add(pair.Key, pair.Value);
        };
    }
}
