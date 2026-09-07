using Constructd.Core.Domain;
namespace Constructd.Core.Configuration;

public static class HostAdminDefaults
{
    public static CapacityConfig Capacity { get; } = new(CapacityMode.Observe, null, 20L << 30, null, null, 60, 600);
    public static UserDefaultsConfig UserDefaults { get; } = new(1, true, 1, null, null, null, null, true, true);
    public static UserCapsConfig UserCaps { get; } = new(null, null, null, null, null, null, null);
    public static LifecycleConfig Lifecycle { get; } = new(300, 30, 600);
    public static MediaConfig Media { get; } = new(16L << 30, 20, 8 << 20, 24, 180, true, null);
    public static NetworkConfig Network { get; } = new(true, true);
    public static UpdatesConfig Updates { get; } = new("permissionBRICK/The-Construct", "main", 60, 120, true, null);
}

public sealed class HostAdminOptions
{
    public HostAdminCapacityOptions Capacity { get; set; } = new();
    public HostAdminUpdatesOptions Updates { get; set; } = new();
    public HostAdminMediaOptions Media { get; set; } = new();
}
public sealed class HostAdminCapacityOptions { public CapacityMode Mode { get; set; } = CapacityMode.Observe; }
public sealed class HostAdminUpdatesOptions { public string? ManifestPublicKey { get; set; } public string? Repository { get; set; } }
public sealed class HostAdminMediaOptions { public string? RootDir { get; set; } }
