namespace Constructd.Core.Abstractions;

// Additive metadata seam: the frozen generic store does not expose the timestamp required by its CAS API.
public sealed record HostConfigSection(string Section, string ValueJson, DateTimeOffset UpdatedAt, string UpdatedBy);
public interface IHostConfigMetadata
{
    Task<IReadOnlyList<HostConfigSection>> ListSectionsAsync(CancellationToken ct);
    /// <summary>Validate all expected timestamps and write the whole request atomically.</summary>
    Task<bool> TrySetSectionsAsync(IReadOnlyList<HostConfigSection> sections,
        IReadOnlyDictionary<string, DateTimeOffset?> expected, CancellationToken ct);
}
