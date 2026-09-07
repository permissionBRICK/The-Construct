using Constructd.Core.Configuration;
using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

/// <summary>Production update authority is installed locally, never selected by a remote caller.</summary>
public static class HostUpdateTrust
{
    public static bool IsPinned(ConstructdOptions options) => !options.Fake ||
        !string.IsNullOrWhiteSpace(options.HostAdmin.Updates.ManifestPublicKey) ||
        !string.IsNullOrWhiteSpace(options.HostAdmin.Updates.Repository);

    public static UpdatesConfig Apply(UpdatesConfig settings, ConstructdOptions options) => !IsPinned(options) ? settings :
        settings with { ManifestPublicKey = options.HostAdmin.Updates.ManifestPublicKey,
            Repository = options.HostAdmin.Updates.Repository ?? HostAdminDefaults.Updates.Repository, RequireSignature = true };
}
