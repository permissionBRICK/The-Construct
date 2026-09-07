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
    public IReadOnlyList<string> ApiFeatures => ["host-admin", "console"];
    public ReleaseInfo()
    {
        var assembly = typeof(ReleaseInfo).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var plus = version.IndexOf('+');
        Installed = new(plus >= 0 ? version[(plus + 1)..] : "unknown", plus >= 0 ? version[..plus] : version, null, "unknown");
    }
}
