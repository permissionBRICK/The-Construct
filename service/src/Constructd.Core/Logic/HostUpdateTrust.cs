using Constructd.Core.Configuration;
using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

/// <summary>Production update authority is installed locally, never selected by a remote caller.</summary>
public static class HostUpdateTrust
{
    public static bool IsPinned(ConstructdOptions options) => !options.Fake ||
        !string.IsNullOrWhiteSpace(options.HostAdmin.Updates.Repository);

    public static UpdatesConfig Apply(UpdatesConfig settings, ConstructdOptions options) => !IsPinned(options) ? settings :
        settings with { Repository = options.HostAdmin.Updates.Repository ?? HostAdminDefaults.Updates.Repository };

    /// <summary>The host's update settings: the stored section, else the defaults with the installer's channel.</summary>
    public static UpdatesConfig Settings(UpdatesConfig? stored, ConstructdOptions options) => Apply(stored ?? Defaults(options), options);

    /// <summary>The settings of a host that stored none: the built-in ones, on the channel its installer set when that names one.</summary>
    public static UpdatesConfig Defaults(ConstructdOptions options) =>
        options.HostAdmin.Updates.Channel is { } channel && ReleaseChannel.IsValid(channel) ? HostAdminDefaults.Updates with { Channel = channel } : HostAdminDefaults.Updates;
}
