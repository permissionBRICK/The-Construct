using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

public interface ICapabilityAggregator
{
    Task<BackendCapabilities> GetAsync(CancellationToken ct);
}

public interface IReleaseInfo
{
    InstalledRelease Installed { get; }
    int SchemaVersion { get; }
    int SchemaMinReadableBy { get; }
    IReadOnlyList<string> ApiFeatures { get; }
}
