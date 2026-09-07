using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

public interface IHostLock
{
    /// <summary>null when another process holds it after <paramref name="wait"/>.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan wait, CancellationToken ct);
    bool IsHeldByAnotherProcess(string name);
}
