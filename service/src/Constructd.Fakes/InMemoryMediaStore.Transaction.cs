namespace Constructd.Fakes;

public sealed partial class InMemoryMediaStore
{
    internal Action SnapshotForRollback()
    {
        var items = _items.ToArray();
        var references = _references.ToArray();
        var uploads = _uploads.ToArray();
        return () =>
        {
            _items.Clear();
            foreach (var pair in items) _items.Add(pair.Key, pair.Value);
            _references.Clear();
            _references.AddRange(references);
            _uploads.Clear();
            foreach (var pair in uploads) _uploads.Add(pair.Key, pair.Value);
        };
    }
}
