using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Fakes;

/// <summary>In-memory append-only audit log.</summary>
public sealed partial class InMemoryAuditLog : IAuditLog
{
    private readonly List<AuditEntry> _entries = [];

    public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        lock (InMemoryTransaction.Gate)
        {
            ArgumentNullException.ThrowIfNull(entry);
            cancellationToken.ThrowIfCancellationRequested();

            {
                _entries.Add(entry);
            }

            return Task.CompletedTask;

        }
    }

    public Task<IReadOnlyList<AuditEntry>> QueryAsync(int limit, CancellationToken cancellationToken)
    {
        lock (InMemoryTransaction.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
}
