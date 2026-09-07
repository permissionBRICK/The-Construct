using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Fakes;

public sealed partial class InMemoryMediaStore : IMediaStore
{
    private readonly Dictionary<string, MediaItem> _items = new();
    private readonly Dictionary<string, MediaUpload> _uploads = new();
    private readonly List<MediaReference> _references = [];
    public Task<MediaItem?> GetAsync(string id, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            return Task.FromResult(_items.GetValueOrDefault(id));
        }
    }
    public Task<IReadOnlyList<MediaItem>> ListAsync(string? owner, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            return Task.FromResult<IReadOnlyList<MediaItem>>(_items.Values.Where(i => owner is null || Ownership.SameName(i.Owner, owner)).ToArray());
        }
    }
    public Task<int> CountByOwnerAsync(string owner, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            return Task.FromResult(_items.Values.Count(i => Ownership.SameName(i.Owner, owner) && i.State is MediaState.Pending or MediaState.Transferring or MediaState.Ready));
        }
    }
    public Task AddAsync(MediaItem item, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            _items.Add(item.Id, item); return Task.CompletedTask;
        }
    }
    public Task<bool> TryTransitionAsync(string id, MediaState expected, MediaItem updated, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            {
                if (!_items.TryGetValue(id, out var old) || old.State != expected || updated.Id != id || !Ownership.SameName(old.Owner, updated.Owner)) return Task.FromResult(false);
                if (updated.State == MediaState.Deleting && _references.Any(r => r.MediaId == id)) return Task.FromResult(false);
                _items[id] = updated; return Task.FromResult(true);
            }

        }
    }
    public Task<bool> RemoveAsync(string id, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            return Task.FromResult(!_references.Any(r => r.MediaId == id) && _items.Remove(id));
        }
    }
    public Task<IReadOnlyList<MediaReference>> ListReferencesAsync(string mediaId, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            return Task.FromResult<IReadOnlyList<MediaReference>>(_references.Where(r => r.MediaId == mediaId).ToArray());
        }
    }
    public Task<IReadOnlyList<MediaReference>> ListReferencesForVmAsync(string vmName, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            return Task.FromResult<IReadOnlyList<MediaReference>>(_references.Where(r => Ownership.SameName(r.VmName, vmName)).ToArray());
        }
    }
    public Task<bool> TryAddReferenceAsync(MediaReference reference, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            {
                if (!_items.TryGetValue(reference.MediaId, out var item) || item.State != MediaState.Ready) return Task.FromResult(false);
                if (!_references.Any(r => r.MediaId == reference.MediaId && Ownership.SameName(r.VmName, reference.VmName) && r.Slot == reference.Slot)) _references.Add(reference);
                _items[item.Id] = item with { LastReferencedAt = reference.Created };
                return Task.FromResult(true);
            }

        }
    }
    public Task<bool> RemoveReferenceAsync(string mediaId, string vmName, MediaSlot slot, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            return Task.FromResult(_references.RemoveAll(r => r.MediaId == mediaId && Ownership.SameName(r.VmName, vmName) && r.Slot == slot) > 0);
        }
    }
    public Task<MediaUpload?> GetUploadAsync(string id, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            return Task.FromResult(_uploads.TryGetValue(id, out var u) ? u with { Received = u.Received.ToArray() } : null);
        }
    }
    public Task AddUploadAsync(MediaUpload upload, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            _uploads.Add(upload.Id, upload with { Received = upload.Received.ToArray() }); return Task.CompletedTask;
        }
    }
    public Task<bool> TryTransitionUploadAsync(string id, UploadState expected, MediaUpload updated, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            {
                if (!_uploads.TryGetValue(id, out var old) || old.State != expected || updated.Id != id) return Task.FromResult(false);
                _uploads[id] = updated with { Received = updated.Received.ToArray() }; return Task.FromResult(true);
            }

        }
    }
    public Task<bool> RecordChunkAsync(string uploadId, int index, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            {
                if (!_uploads.TryGetValue(uploadId, out var u) || u.State != UploadState.Open || u.ChunkBytes <= 0 || index < 0 || index >= (u.SizeBytes + u.ChunkBytes - 1) / u.ChunkBytes) return Task.FromResult(false);
                _uploads[uploadId] = u with { Received = u.Received.Append(index).Distinct().Order().ToArray() }; return Task.FromResult(true);
            }

        }
    }
    public Task<bool> CompleteUploadAsync(string uploadId, MediaItem ready, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock(InMemoryTransaction.Gate)
        {
            if(!_uploads.TryGetValue(uploadId,out var upload) || upload.State != UploadState.Completing || upload.MediaId != ready.Id ||
                !_items.TryGetValue(ready.Id,out var item) || item.State != MediaState.Transferring || ready.State != MediaState.Ready ||
                !Ownership.SameName(item.Owner,ready.Owner)) return Task.FromResult(false);
            _items[ready.Id]=ready; _uploads[uploadId]=upload with {State=UploadState.Done}; return Task.FromResult(true);
        }
    }
    public Task<IReadOnlyList<MediaUpload>> ListExpiredUploadsAsync(DateTimeOffset now, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            return Task.FromResult<IReadOnlyList<MediaUpload>>(_uploads.Values.Where(u => u.State == UploadState.Open && u.ExpiresAt <= now).ToArray());
        }
    }
}
