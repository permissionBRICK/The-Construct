using System.Text.RegularExpressions;
using Constructd.Core.Abstractions;
namespace Constructd.Core.Logic;

public sealed class UpdateException(string code) : Exception(code), IConstructdError
{
    public string Code { get; } = code;
}

public static class ManifestRules
{
    public static bool Hash(string? value) => value is not null && Regex.IsMatch(value, "^[0-9a-f]{64}$");
    public static void Validate(ReleaseManifest m, string repository, ReleaseDescriptor release)
    {
        if (m.SchemaVersion != 1) throw new UpdateException("incompatible");
        if (!Regex.IsMatch(m.Commit ?? "", "^[0-9a-f]{40}$") || m.Ref != "refs/heads/main" ||
            m.Repository != repository || m.ReleaseTag != "host-" + m.Commit || release.Tag != m.ReleaseTag ||
            release.Commit != m.Commit || m.PayloadAsset != $"construct-host-{m.Commit[..7]}-win-x64.zip" ||
            m.UpdaterPath != "updater/Update-ConstructHost.ps1" || !Hash(m.PayloadSha256) || !Hash(m.SumsSha256) ||
            !Hash(m.UpdaterSha256) || m.Database is null || m.Config is null || m.Compat is null ||
            m.Database.BreakingMigrations is null || m.Config.RequiredKeys is null ||
            m.Database.MinReadableBy < 0 || m.Database.MinReadableBy > m.Database.SchemaVersion ||
            m.Config.MinReadableBy < 0 || m.Config.MinReadableBy > m.Config.SettingsSchemaVersion)
            throw new UpdateException("incompatible");
    }
}

public static class UpdateCompatibility
{
    public static string[] Reasons(ReleaseManifest m, int schema, int minReadable, int settingsMinReadable, Func<string, bool> hasKey)
    {
        var reasons = new List<string>();
        if (m.Database.SchemaVersion < minReadable || m.Config.SettingsSchemaVersion < settingsMinReadable)
            reasons.Add("unsupported-downgrade");
        if (m.Compat.MinSchemaVersionToUpdateFrom > schema || m.Config.RequiredKeys.Any(k => !hasKey(k)))
            reasons.Add("incompatible");
        return reasons.ToArray();
    }
    public static bool RequiresDatabaseRestore(ReleaseManifest m, int previousSchema) =>
        m.Database.MinReadableBy > previousSchema || m.Database.BreakingMigrations.Count > 0;
}
