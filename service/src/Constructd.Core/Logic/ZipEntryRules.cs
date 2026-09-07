using System.Text.RegularExpressions;
namespace Constructd.Core.Logic;

public static class ZipEntryRules
{
    // Windows normalization matters even when a release is verified on Linux.
    public static bool IsSafe(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path.Contains('\\') || path.StartsWith('/') ||
            path.Any(c => c < 32 || ":*?\"<>|".Contains(c))) return false;
        return path.Split('/').All(p => p.Length > 0 && p is not "." and not ".." &&
            !p.EndsWith('.') && !p.EndsWith(' ') &&
            !Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase));
    }
    public static bool IsPayloadFile(string path) => IsSafe(path) &&
        (path.StartsWith("service/", StringComparison.Ordinal) || path.StartsWith("scripts/", StringComparison.Ordinal) || path == "updater/Update-ConstructHost.ps1");
    public static bool IsPreserved(string path) => path.Split('/').Any(p =>
        p.Equals("appsettings.Production.json", StringComparison.OrdinalIgnoreCase) ||
        p.Equals("install.json", StringComparison.OrdinalIgnoreCase) || p.Contains(".db", StringComparison.OrdinalIgnoreCase) ||
        new[] { "data", "media", "iso", ".construct-tools", "keys", "settings.json", "projects", ".git" }.Contains(p, StringComparer.OrdinalIgnoreCase));
}
