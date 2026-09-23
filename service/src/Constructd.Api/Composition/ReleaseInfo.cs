using System.Reflection;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Sqlite.Migrations;
namespace Constructd.Api.Composition;

public sealed class ReleaseInfo : IReleaseInfo
{
    private sealed record CachedRelease(DateTime LastWriteTimeUtc, InstalledRelease Release);
    private readonly string installRecordPath;
    private readonly InstalledRelease baseline;
    private CachedRelease? cached;
    public InstalledRelease Installed
    {
        get
        {
            var stamp = File.GetLastWriteTimeUtc(installRecordPath);
            var current = Volatile.Read(ref cached);
            if (current?.LastWriteTimeUtc == stamp) return current.Release;
            var release = baseline;
            try
            {
                if (File.Exists(installRecordPath))
                {
                    var record = System.Text.Json.JsonSerializer.Deserialize<Constructd.Windows.Updates.InstallRecord>(File.ReadAllText(installRecordPath),Constructd.Windows.Updates.UpdateFiles.Json);
                    if(record?.Commit == baseline.Commit) release=baseline with {PackageVersion=record.PackageVersion, InstalledAt=record.InstalledAt, Source="release"};
                }
            }
            catch (Exception ex) when(ex is IOException or System.Text.Json.JsonException) { }
            Volatile.Write(ref cached, new(stamp, release));
            return release;
        }
    }
    public int SchemaVersion => SqliteMigrations.SchemaVersion;
    public int SchemaMinReadableBy => SqliteMigrations.MinReadableBy;
    /// <summary>
    /// Implemented API groups. Individual capabilities describe platform limitations within them;
    /// `network-mode` (relayed or direct guest networking) exists on Proxmox only.
    /// </summary>
    public IReadOnlyList<string> ApiFeatures => options?.IsProxmox == true
        ? ["host-admin", "windows-guests", "usage", "updates", "primary-cpu", "primary-memory", "primary-nested", "network-mode", "children", "media", "console", "network", .. SourceCache]
        : ["host-admin", "windows-guests", "usage", "children", "media", "console", "updates", "network", "primary-cpu", "primary-memory", "primary-nested", .. SourceCache];
    private string[] SourceCache => options?.HostAdmin.Source.Enabled != false ? ["source-cache"] : [];
    private readonly Constructd.Core.Configuration.ConstructdOptions? options;
    public ReleaseInfo(Constructd.Core.Configuration.ConstructdOptions? options = null, string? installRecordPath = null)
    {
        this.options = options;
        this.installRecordPath = installRecordPath ?? Path.Combine(AppContext.BaseDirectory,"install.json");
        var assembly = typeof(ReleaseInfo).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var plus = version.IndexOf('+');
        baseline = new(plus >= 0 ? version[(plus + 1)..] : "unknown", plus >= 0 ? version[..plus] : version, null, "installer");
    }
}
