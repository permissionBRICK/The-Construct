using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Fakes;

/// <summary>In-memory append-only audit log.</summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    private readonly Lock _gate = new();
    private readonly List<AuditEntry> _entries = [];

    public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> QueryAsync(int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            IReadOnlyList<AuditEntry> page = _entries
                .AsEnumerable()
                .Reverse()
                .Take(Math.Max(0, limit))
                .ToList();

            return Task.FromResult(page);
        }
    }
}
