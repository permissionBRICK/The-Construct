using Constructd.Core.Domain;
namespace Constructd.Api.Contracts;
public sealed record AcquireMediaRequest(string Url, string? Name, string Role, string? ExpectedSha256 = null, string? DedicatedTo = null, string? OperationKey = null);
public sealed record BeginMediaUploadRequest(string Name, string Role, long SizeBytes, string? ExpectedSha256 = null, string? DedicatedTo = null, string? OperationKey = null);
public sealed record MediaItemResponse(string Id,string Owner,string Name,MediaRole Role,MediaSource Source,string? SourceUrl,
    MediaState State,long? SizeBytes,long ReservedBytes,string? Sha256,string? ExpectedSha256,string? Error,string? JobId,string? DedicatedTo,
    DateTimeOffset Created,DateTimeOffset? ReadyAt,int References)
{
    public static MediaItemResponse From(MediaItem item,int references) => new(item.Id,item.Owner,item.Name,item.Role,item.Source,item.SourceUrl,item.State,item.SizeBytes,item.ReservedBytes,item.Sha256,item.ExpectedSha256,item.Error,item.JobId,item.DedicatedTo,item.Created,item.ReadyAt,references);
}

public sealed record MediaCleanupRetained(string Id,string Reason);
public sealed record MediaCleanupResult(IReadOnlyList<string> Removed,IReadOnlyList<MediaCleanupRetained> Retained);
