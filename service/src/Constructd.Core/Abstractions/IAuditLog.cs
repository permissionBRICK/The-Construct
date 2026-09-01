using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

/// <summary>Append-only audit trail; readable by admins through <c>GET /audit</c>.</summary>
public interface IAuditLog
{
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken);

    /// <summary>The most recent entries, newest first.</summary>
    Task<IReadOnlyList<AuditEntry>> QueryAsync(int limit, CancellationToken cancellationToken);
}
