using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

public interface IMediaStore
{
    Task<MediaItem?> GetAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<MediaItem>> ListAsync(string? owner, CancellationToken ct);
    Task<int> CountByOwnerAsync(string owner, CancellationToken ct);
    Task AddAsync(MediaItem item, CancellationToken ct);
    /// <summary>Compare-and-set on State: false when the item is no longer in <paramref name="expected"/>.</summary>
    Task<bool> TryTransitionAsync(string id, MediaState expected, MediaItem updated, CancellationToken ct);
    Task<bool> RemoveAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<MediaReference>> ListReferencesAsync(string mediaId, CancellationToken ct);
    Task<IReadOnlyList<MediaReference>> ListReferencesForVmAsync(string vmName, CancellationToken ct);
    /// <summary>Insert only while the item is Ready and not Deleting; false otherwise.</summary>
    Task<bool> TryAddReferenceAsync(MediaReference reference, CancellationToken ct);
    Task<bool> RemoveReferenceAsync(string mediaId, string vmName, MediaSlot slot, CancellationToken ct);
    Task<MediaUpload?> GetUploadAsync(string id, CancellationToken ct);
    Task AddUploadAsync(MediaUpload upload, CancellationToken ct);
    Task<bool> TryTransitionUploadAsync(string id, UploadState expected, MediaUpload updated, CancellationToken ct);
    Task<bool> RecordChunkAsync(string uploadId, int index, CancellationToken ct);
    Task<IReadOnlyList<MediaUpload>> ListExpiredUploadsAsync(DateTimeOffset now, CancellationToken ct);
}

public sealed record UrlAdmission(bool Allowed, string? Reason, string? Address, Uri? Normalized, IReadOnlyList<string> ResolvedAddresses);
public interface IUrlAdmissionPolicy
{
    /// <summary>Pure rules of §6.3 over already-resolved addresses; no I/O.</summary>
    UrlAdmission Check(Uri url, IReadOnlyList<System.Net.IPAddress> resolved, bool allowHttp, bool hasChecksum);
}

public sealed record TransferResult(long SizeBytes, string Sha256, Uri FinalUrl);
public interface IMediaTransfer
{
    Task<TransferResult> AcquireAsync(MediaItem item, Uri source, long maxBytes, TimeSpan timeout, IProgress<string>? progress, CancellationToken ct);
    Task WriteChunkAsync(MediaUpload upload, int index, Stream body, long contentLength, CancellationToken ct);
    Task<string> HashAsync(string path, IProgress<string>? progress, CancellationToken ct);
    Task<bool> LooksLikeIsoAsync(string path, CancellationToken ct);
    /// <summary>false = held open by the hypervisor (retry later); throws for other errors.</summary>
    Task<bool> TryDeleteAsync(string path, CancellationToken ct);
    Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken ct);
}
