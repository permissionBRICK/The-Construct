using System.Collections.Concurrent;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Fakes;

/// <summary>In-memory forward state; the durable one is a SQLite table.</summary>
public sealed class InMemoryForwardStore : IForwardStore
{
    private readonly ConcurrentDictionary<string, PortForward> _forwards = new(StringComparer.Ordinal);

    public Task<PortForward?> GetAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_forwards.TryGetValue(id, out var forward) ? forward : null);
    }

    public Task<IReadOnlyList<PortForward>> ListAsync(string? vmName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<PortForward> result = _forwards.Values
            .Where(f => vmName is null || string.Equals(f.VmName, vmName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Created)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult(result);
    }

    public Task<int> CountByVmAsync(string vmName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_forwards.Values.Count(
            f => string.Equals(f.VmName, vmName, StringComparison.OrdinalIgnoreCase)));
    }

    public Task AddAsync(PortForward forward, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(forward);
        cancellationToken.ThrowIfCancellationRequested();
        _forwards[forward.Id] = forward;
        return Task.CompletedTask;
    }

    public Task<bool> SetAckAsync(string id, ForwardAck ack, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ack);
        cancellationToken.ThrowIfCancellationRequested();

        // A compare-and-swap loop rather than a read-then-write: two windows may ack the same
        // forward at the same moment, and the loser must overwrite a value it actually saw.
        while (_forwards.TryGetValue(id, out var existing))
        {
            if (_forwards.TryUpdate(id, existing with { Ack = ack }, existing))
            {
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    public Task<bool> RemoveAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_forwards.TryRemove(id, out _));
    }
}
