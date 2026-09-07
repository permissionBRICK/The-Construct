namespace Constructd.Core.Domain;

/// <param name="DedicatedTo">VM the item was uploaded for; deleted with that VM (§6.5).</param>
public sealed record MediaItem(
    string Id,
    string Owner,
    string Name,
    MediaRole Role,
    MediaSource Source,
    string? SourceUrl,
    string Path,
    MediaState State,
    long? SizeBytes,
    long ReservedBytes,
    string? Sha256,
    string? ExpectedSha256,
    string? Error,
    string? JobId,
    string? DedicatedTo,
    DateTimeOffset Created,
    DateTimeOffset? ReadyAt,
    DateTimeOffset? LastReferencedAt);

public sealed record MediaReference(string MediaId, string VmName, MediaSlot Slot, DateTimeOffset Created);

public sealed record MediaUpload(
    string Id,
    string MediaId,
    string Owner,
    long SizeBytes,
    int ChunkBytes,
    IReadOnlyList<int> Received,
    UploadState State,
    string? OperationKey,
    DateTimeOffset Created,
    DateTimeOffset ExpiresAt);
