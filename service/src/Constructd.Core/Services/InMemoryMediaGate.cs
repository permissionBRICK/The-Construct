using Constructd.Core.Abstractions;
namespace Constructd.Core.Services;

public sealed class InMemoryMediaGate : IMediaGate
{
    private readonly InMemoryVmOperationGate _gate = new();
    public Task<IAsyncDisposable> AcquireAsync(string mediaId, string operationId, CancellationToken ct) => _gate.AcquireAsync(mediaId, operationId, ct);
    public async Task<IAsyncDisposable> AcquireManyAsync(IReadOnlyList<string> mediaIds, string operationId, CancellationToken ct)
    {
        var handles = new List<IAsyncDisposable>();
        try
        {
            foreach (var id in mediaIds.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                handles.Add(await AcquireAsync(id, operationId, ct));
            return new Handles(handles);
        }
        catch { await new Handles(handles).DisposeAsync(); throw; }
    }
    private sealed class Handles(List<IAsyncDisposable> handles) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        { for (var i = handles.Count - 1; i >= 0; i--) await handles[i].DisposeAsync(); }
    }
}
