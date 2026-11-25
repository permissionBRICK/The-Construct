using Constructd.Core.Configuration;

namespace Constructd.Core.Logic;

/// <summary>
/// Validation and normalization for Windows VM storage directories (<c>storage.vmStorageRoot</c> and per-VM move
/// destinations). Only absolute drive-letter directories are accepted: UNC and relative paths are refused because the
/// Hyper-V service runs as <c>LocalSystem</c> with <c>%SystemRoot%\System32</c> as its working directory.
/// </summary>
public static class StoragePathRules
{
    public const string DefaultPrimaryRoot = @"C:\ProgramData\Microsoft\Windows\Virtual Hard Disks";

    public const string Rule = "Expected an absolute Windows folder path on a drive letter (e.g. D:\\ConstructVMs), without quotes, wildcards or relative segments.";

    private static readonly char[] InvalidChars = ['"', '<', '>', '|', '?', '*'];

    /// <summary>Returns null when <paramref name="path"/> is acceptable (or blank when <paramref name="allowEmpty"/> is true).</summary>
    public static string? Problem(string? path, bool allowEmpty = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return allowEmpty ? null : Rule;
        var value = path.Trim();
        if (value.Length is < 3 or > 4096) return Rule;
        if (!char.IsAsciiLetter(value[0]) || value[1] != ':' || (value[2] != '\\' && value[2] != '/')) return Rule;
        if (value.Any(c => char.IsControl(c) || InvalidChars.Contains(c))) return Rule;
        var rest = value[3..].Replace('/', '\\');
        if (rest.Contains(':', StringComparison.Ordinal)) return Rule;
        foreach (var segment in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')) return Rule;
        }
        return null;
    }

    /// <summary>Normalizes a valid Windows directory path to backslashes without a trailing slash (except <c>D:\</c>).</summary>
    public static string NormalizeDirectory(string path)
    {
        var value = path.Trim().Replace('/', '\\');
        var drive = char.ToUpperInvariant(value[0]) + ":\\";
        var rest = value[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return rest.Length == 0 ? drive : drive + string.Join('\\', rest);
    }

    /// <summary>Normalizes an optional directory path; blank becomes null.</summary>
    public static string? NormalizeOptional(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : NormalizeDirectory(path);

    /// <summary>The drive volume root (e.g. <c>D:\</c>) of a valid Windows path.</summary>
    public static string VolumeRoot(string path) =>
        char.ToUpperInvariant(path.Trim()[0]) + ":\\";

    /// <summary>The directory holding <paramref name="filePath"/>, normalized, or null if not a Windows path.</summary>
    public static string? DirectoryOf(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var v = filePath.Trim().Replace('/', '\\');
        if (v.Length < 3 || !char.IsAsciiLetter(v[0]) || v[1] != ':' || v[2] != '\\') return null;
        var last = v.LastIndexOf('\\');
        return last <= 2 ? char.ToUpperInvariant(v[0]) + ":\\" : NormalizeDirectory(v[..last]);
    }

    /// <summary>Resolves the effective host default storage root: stored host config → startup option → null (Hyper-V default).</summary>
    public static string? ResolveConfiguredRoot(string? storedRoot, ConstructdOptions options) =>
        NormalizeOptional(storedRoot) ?? NormalizeOptional(options.VmStorageRoot);

    /// <summary>Resolves the effective concrete directory for primary VMs: stored config → startup option → Hyper-V default folder.</summary>
    public static string ResolveEffectivePrimaryDirectory(string? storedRoot, ConstructdOptions options) =>
        ResolveConfiguredRoot(storedRoot, options) ?? DefaultPrimaryRoot;

    /// <summary>Case-insensitive comparison of two normalized Windows directory paths.</summary>
    public static bool SameDirectory(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(NormalizeDirectory(a), NormalizeDirectory(b), StringComparison.OrdinalIgnoreCase);
}
