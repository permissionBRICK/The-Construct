namespace Constructd.Fakes;

public sealed partial class InMemoryAuditLog
{
    internal Action SnapshotForRollback()
    {
        var entries = _entries.ToArray();
        return () =>
        {
            _entries.Clear();
            _entries.AddRange(entries);
        };
    }
}
