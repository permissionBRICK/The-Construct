using System.Reflection;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Sqlite.Migrations;
namespace Constructd.Api.Composition;

public sealed class ReleaseInfo : IReleaseInfo
{
    public InstalledRelease Installed { get; }
    public int SchemaVersion => SqliteMigrations.SchemaVersion;
    public int SchemaMinReadableBy => SqliteMigrations.MinReadableBy;
    /// <summary>
    /// What this host's platform really offers. A Proxmox node has no child VMs, media catalog,
    /// screenshot console, self-update or guest-network policy yet, and must not advertise them: the
    /// extension shows what is listed here and hides the rest (host-administration contract §8.1).
    /// </summary>
    public IReadOnlyList<string> ApiFeatures => options?.IsProxmox == true
        ? ["host-admin", "primary-cpu", "primary-memory", "network-mode", .. SourceCache]
        : ["host-admin", "children", "media", "console", "updates", "network", "primary-cpu", "primary-memory", .. SourceCache];
    private string[] SourceCache => options?.HostAdmin.Source.Enabled != false ? ["source-cache"] : [];
    private readonly Constructd.Core.Configuration.ConstructdOptions? options;
    public ReleaseInfo(Constructd.Core.Configuration.ConstructdOptions? options = null)
    {
        this.options = options;
        var assembly = typeof(ReleaseInfo).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var plus = version.IndexOf('+');
        Installed = new(plus >= 0 ? version[(plus + 1)..] : "unknown", plus >= 0 ? version[..plus] : version, null, "installer");
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory,"install.json");
            if (File.Exists(path))
            {
                var record = System.Text.Json.JsonSerializer.Deserialize<Constructd.Windows.Updates.InstallRecord>(File.ReadAllText(path),Constructd.Windows.Updates.UpdateFiles.Json);
                if(record?.Commit == Installed.Commit) Installed=Installed with {PackageVersion=record.PackageVersion, InstalledAt=record.InstalledAt, Source="release"};
            }
        }
        catch (Exception ex) when(ex is IOException or System.Text.Json.JsonException) { }
    }
}
