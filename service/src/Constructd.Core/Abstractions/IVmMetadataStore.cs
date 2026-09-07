using Constructd.Core.Domain;
namespace Constructd.Core.Abstractions;

/// <summary>Field-specific writes for metadata excluded from the legacy whole-row update.</summary>
public interface IVmMetadataStore
{
    Task<bool> UpdateIncarnationAsync(string name, string incarnation, CancellationToken ct);
    Task<bool> SetTokenAsync(string name, string? hash, VmTokenKind kind, CancellationToken ct);
}
