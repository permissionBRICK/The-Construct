using Constructd.Core.Abstractions;

namespace Constructd.Core.Services;

/// <summary>
/// The ISO catalog of a platform that installs from no ISO at all (Proxmox VMs are cloned from a
/// cloud image and seeded with cloud-init). Reads answer "nothing published" so the catalog routes
/// and <c>admin iso status</c> stay honest; anything that would produce media is refused.
/// </summary>
public sealed class UnsupportedIsoCatalog : IIsoCatalog
{
    public IsoCatalogEntry? GetCurrent() => null;

    public IReadOnlyList<IsoCatalogEntry> List() => [];

    public string NextMediaPath() => throw Unsupported();

    public IsoCatalogEntry Publish(string isoPath, IsoSidecar sidecar) => throw Unsupported();

    public IsoPruneResult Prune() => new([], []);

    private static NotSupportedException Unsupported() =>
        new("This host installs VMs from a cloud image, not from autoinstall media; there is no ISO catalog.");
}
