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
    public IReadOnlyList<string> ApiFeatures => ["host-admin", "children", "media", "console", "updates", "network", "primary-cpu", .. (options?.HostAdmin.Source.Enabled != false ? new[] { "source-cache" } : [])];
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
