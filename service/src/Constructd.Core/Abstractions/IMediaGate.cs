using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

public interface IMediaGate
{
    Task<IAsyncDisposable> AcquireAsync(string mediaId, string operationId, CancellationToken ct);
    /// <summary>Acquires several media gates in ascending id order.</summary>
    Task<IAsyncDisposable> AcquireManyAsync(IReadOnlyList<string> mediaIds, string operationId, CancellationToken ct);
}
