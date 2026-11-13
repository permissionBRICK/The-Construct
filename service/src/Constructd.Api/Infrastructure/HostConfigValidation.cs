using System.Text.Json;
using System.Text.RegularExpressions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Infrastructure;

public static partial class HostConfigValidation
{
    public static readonly IReadOnlyDictionary<string, object> Defaults = new Dictionary<string, object>
    {
        ["capacity"] = HostAdminDefaults.Capacity,
        ["usage"] = HostAdminDefaults.Usage,
        ["memoryPressure"] = HostAdminDefaults.MemoryPressure,
        ["userDefaults"] = HostAdminDefaults.UserDefaults,
        ["userCaps"] = HostAdminDefaults.UserCaps,
        ["lifecycle"] = HostAdminDefaults.Lifecycle,
        ["media"] = HostAdminDefaults.Media,
        ["network"] = HostAdminDefaults.Network,
        ["virtualization"] = HostAdminDefaults.Virtualization,
        ["updates"] = HostAdminDefaults.Updates,
        ["vault"] = HostAdminDefaults.Vault,
        ["https"] = HostAdminDefaults.Https,
        ["backups"] = HostAdminDefaults.Backups,
    };
    public static string? Allowance(UserAllowance a) => a.MaxRetainedChildren < 0 || a.CpuBudget < 0 || a.RamBudgetBytes < 0 || a.StorageBudgetBytes < 0
        ? "Budgets and counts must be non-negative." : a.MaxChildLifetimeSeconds is < 300 ? "A finite lifetime limit must be at least 300 seconds." : null;
    public static string? Validate(object value) => value switch
    {
        UsageConfig u when u.RetentionDays is < 1 or > 36500 => "Usage retention must be between 1 and 36500 days.",
        NetworkConfig n when n.DefaultMode is not ("relayed" or "direct") => "Network defaultMode must be relayed or direct.",
        MemoryPressureConfig m when m.LowWaterPercent <= 0 || m.LowWaterPercent >= m.HighWaterPercent || m.HighWaterPercent > 100 ||
            m.SwapHighWaterPercent is < 0 or > 100 || m.MinSecondsBetweenSaves < 1 || m.CooldownMinutesAfterSave < 0 =>
            "Memory pressure requires 0 < low < high <= 100, swap 0–100, a positive save interval and a non-negative cooldown.",
        CapacityConfig c when !Enum.IsDefined(c.Mode) || c.RamHeadroomBytes < 0 || c.StorageHeadroomBytes < 0 || c.CpuBudget < 0 || c.MaxVcpusPerVm < 1 ||
            c.ReconcileSeconds < 30 || c.OrphanReservationTimeoutSeconds < 30 => "Capacity limits must be non-negative and timeouts at least 30 seconds.",
        UserDefaultsConfig d when d.MaxPrimaries < 0 => "Primary count must be non-negative.",
        UserDefaultsConfig d => Allowance(new(d.AllowChildCreation, d.MaxRetainedChildren, d.CpuBudget, d.RamBudgetBytes, d.StorageBudgetBytes, d.MaxChildLifetimeSeconds, d.AllowNeverLifetime, d.AllowSharing)),
        UserCapsConfig c => Allowance(new(null, c.MaxRetainedChildren, c.CpuBudget, c.RamBudgetBytes, c.StorageBudgetBytes, c.MaxChildLifetimeSeconds, c.AllowNeverLifetime, c.AllowSharing)),
        LifecycleConfig l when l.GracefulShutdownTimeoutSeconds < 30 || l.LeaseTickSeconds < 30 || l.LeaseRetrySeconds < 30 => "Lifecycle timeouts must be at least 30 seconds.",
        MediaConfig m when m.MaxBytes < 1 || m.MaxItemsPerUser < 1 || m.UploadChunkBytes < 1048576 || m.UploadChunkBytes > 67108864 || m.UploadTtlHours < 1 || m.AcquireTimeoutMinutes < 1 || m.UnreferencedTtlHours < 1 => "Media sizes/counts must be positive; chunks must be 1–64 MiB.",
        VaultConfig v when VaultWebAddress.Problem(v.WebUrl) is { } problem => problem,
        HttpsConfig h when h.SharedPort is < 1 or > 65535 => "The shared HTTPS port must be null or between 1 and 65535.",
        BackupsConfig b when b.IntervalHours is < 0 or > 720 || b.Keep < 1 || b.Keep > b.MaxKeep || b.MaxKeep > 50 ||
            b.PinDays is < 1 or > 365 || b.RetainDeletedDays is < 0 or > 3650 || b.MinFreeBytes < 0 || b.MaxBytesPerVm is < 64L << 20 =>
            "Backups require an interval of 0-720 hours, 1 <= keep <= maxKeep <= 50, 1-365 pin days, 0-3650 retention days, " +
            "a non-negative free-space reserve and a per-VM limit of null or at least 64 MiB.",
        VirtualizationConfig v when v.MinimumRamBytes is < 1L << 30 => "The minimum RAM must be null or at least 1 GiB.",
        UpdatesConfig u when string.IsNullOrEmpty(u.Repository) || !RepositoryPattern().IsMatch(u.Repository) || !ReleaseChannel.IsValid(u.Channel) || u.DrainTimeoutMinutes < 1 || u.HealthTimeoutSeconds < 30 =>
            "Updates require owner/repository, a channel (stable, nightly or an https manifest URL; main means stable) and positive timeouts (health at least 30 seconds).",
        _ => null,
    };
    /// <summary>The shared HTTPS port must not collide with a port this service already owns.</summary>
    public static string? HttpsPortConflict(HttpsConfig https, ConstructdOptions options)
    {
        if (https.SharedPort is not int port) return null;
        if (port >= options.SshForwardPorts.Start && port <= options.SshForwardPorts.End ||
            port >= options.AppForwardPorts.Start && port <= options.AppForwardPorts.End)
            return $"Port {port} is inside a forward port range.";
        return port == options.ApiPort() ? $"Port {port} is the API's own port." : null;
    }
    public static bool UnsupportedOnPlatform(object value, bool isProxmox) =>
        value is NetworkConfig { DefaultMode: "direct" } && !isProxmox;
    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryPattern();
}
