using Constructd.Core.Domain;
namespace Constructd.Core.Abstractions;

/// <summary>Field-specific writes for metadata excluded from the legacy whole-row update.</summary>
public interface IVmMetadataStore
{
    /// <summary>Undo only an unqueued legacy primary deletion. Caller holds the VM operation gate; never used by state refreshes.</summary>
    Task<bool> RestoreUnqueuedDeletionAsync(Vm original, CancellationToken ct);
    Task<bool> UpdateIncarnationAsync(string name, string incarnation, CancellationToken ct);
    Task<bool> SetTokenAsync(string name, string? hash, VmTokenKind kind, CancellationToken ct);
}
