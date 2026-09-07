using System.Text.Json;
using System.Text.RegularExpressions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
namespace Constructd.Api.Infrastructure;

public static partial class HostConfigValidation
{
    public static readonly IReadOnlyDictionary<string, object> Defaults = new Dictionary<string, object>
    {
        ["capacity"] = HostAdminDefaults.Capacity,
        ["userDefaults"] = HostAdminDefaults.UserDefaults,
        ["userCaps"] = HostAdminDefaults.UserCaps,
        ["lifecycle"] = HostAdminDefaults.Lifecycle,
        ["media"] = HostAdminDefaults.Media,
        ["network"] = HostAdminDefaults.Network,
        ["updates"] = HostAdminDefaults.Updates,
    };
    public static string? Allowance(UserAllowance a) => a.MaxRetainedChildren < 0 || a.CpuBudget < 0 || a.RamBudgetBytes < 0 || a.StorageBudgetBytes < 0
        ? "Budgets and counts must be non-negative." : a.MaxChildLifetimeSeconds is < 300 ? "A finite lifetime limit must be at least 300 seconds." : null;
    public static string? Validate(object value) => value switch
    {
        CapacityConfig c when !Enum.IsDefined(c.Mode) || c.RamHeadroomBytes < 0 || c.StorageHeadroomBytes < 0 || c.CpuBudget < 0 || c.MaxVcpusPerVm < 1 ||
            c.ReconcileSeconds < 30 || c.OrphanReservationTimeoutSeconds < 30 => "Capacity limits must be non-negative and timeouts at least 30 seconds.",
        UserDefaultsConfig d when d.MaxPrimaries < 0 => "Primary count must be non-negative.",
        UserDefaultsConfig d => Allowance(new(d.AllowChildCreation, d.MaxRetainedChildren, d.CpuBudget, d.RamBudgetBytes, d.StorageBudgetBytes, d.MaxChildLifetimeSeconds, d.AllowNeverLifetime, d.AllowSharing)),
        UserCapsConfig c => Allowance(new(null, c.MaxRetainedChildren, c.CpuBudget, c.RamBudgetBytes, c.StorageBudgetBytes, c.MaxChildLifetimeSeconds, c.AllowNeverLifetime, c.AllowSharing)),
        LifecycleConfig l when l.GracefulShutdownTimeoutSeconds < 30 || l.LeaseTickSeconds < 30 || l.LeaseRetrySeconds < 30 => "Lifecycle timeouts must be at least 30 seconds.",
        MediaConfig m when m.MaxBytes < 1 || m.MaxItemsPerUser < 1 || m.UploadChunkBytes < 1048576 || m.UploadChunkBytes > 67108864 || m.UploadTtlHours < 1 || m.AcquireTimeoutMinutes < 1 || m.UnreferencedTtlHours < 1 => "Media sizes/counts must be positive; chunks must be 1–64 MiB.",
        UpdatesConfig u when string.IsNullOrEmpty(u.Repository) || !RepositoryPattern().IsMatch(u.Repository) || u.Channel != "main" || u.DrainTimeoutMinutes < 1 || u.HealthTimeoutSeconds < 30 => "Updates require owner/repository, main channel and positive timeouts (health at least 30 seconds).",
        UpdatesConfig u when u.ManifestPublicKey is not null && !ValidKey(u.ManifestPublicKey) => "Manifest public key must be base64 encoding of 32 bytes.",
        _ => null,
    };
    public static bool ValidKey(string text)
    { Span<byte> bytes = stackalloc byte[32]; return Convert.TryFromBase64String(text, bytes, out var written) && written == 32; }
    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryPattern();
}
